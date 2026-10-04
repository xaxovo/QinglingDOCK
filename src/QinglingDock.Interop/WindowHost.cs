using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace QinglingDock.Interop;

/// <summary>
/// 窗口消息处理器。委托实例保存在静态字段中，防止被 GC 回收——
/// 窗口过程被回收会导致进程直接崩溃（Win32 互操作高发问题）。
/// </summary>
public static unsafe class WndProcHandler
{
    /// <summary>持有委托引用，生命周期与进程一致。</summary>
    private static readonly WNDPROC Proc = Dispatch;

    /// <summary>返回可放入 WNDCLASSEXW 的窗口过程委托（WNDPROC 为内部类型，故本成员为 internal）。</summary>
    internal static WNDPROC Handler => Proc;

    private static LRESULT Dispatch(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        // 注意：WM_NCCREATE / WM_CREATE 早于 CreateWindowEx 返回，此时 GWLP_USERDATA 尚未写入，
        // 因此这两个消息必然走到 DefWindowProc。宿主初始化由 NativeWindowHost.Run 在
        // 窗口创建成功后显式调用，不依赖 WM_CREATE —— 否则初始化会被此处静默吞掉。
        var userData = WindowApi.GetWindowLongPtr(hwnd, WindowApi.GwlpUserData);
        if (userData == 0)
        {
            return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }

        var host = (NativeWindowHost?)GCHandle.FromIntPtr(userData).Target;

        if (host is null)
        {
            return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }

        return (LRESULT)host.HandleMessage((nint)hwnd.Value, message, (nint)wParam.Value, lParam.Value);
    }
}

/// <summary>由 Win32 窗口宿主实现，接收原生窗口消息与生命周期通知。</summary>
public interface IWindowMessageSink
{
    /// <summary>窗口已创建且宿主已就绪，此时可安全建立与 HWND 绑定的资源。</summary>
    void OnWindowCreated(nint hwnd);

    /// <summary>处理原生窗口消息，返回值作为窗口过程结果。</summary>
    nint HandleMessage(nint hwnd, uint message, nint wParam, nint lParam);
}

/// <summary>
/// 原生窗口宿主：注册窗口类、创建窗口、运行消息循环。
/// 刻意不使用 WPF/WinUI 的托管窗口，因为置顶策略、每屏 DPI、点击穿透与
/// 全屏让位都需要直接控制 WM_* 消息（见 docs/PLAN.md §2.4）。
/// </summary>
public sealed unsafe class NativeWindowHost
{
    private const string WindowClassName = "QinglingDock.NativeWindow";

    /// <summary>窗口类名字符串常驻非托管内存，供 PCWSTR 引用（注册后仍需保持有效）。</summary>
    private static readonly nint ClassNameBuffer = Marshal.StringToHGlobalUni(WindowClassName);

    private readonly IWindowMessageSink _sink;
    private GCHandle _selfHandle;

    private NativeWindowHost(IWindowMessageSink sink) => _sink = sink;

    /// <summary>窗口句柄（原生值）。</summary>
    public nint Handle { get; private set; }

    /// <summary>消息泵出错时的回调（参数为 Win32 错误码）。</summary>
    public static Action<int>? OnMessageLoopError { get; set; }

    /// <summary>检测到同一消息洪泛时的回调（参数为消息编号）。</summary>
    public static Action<uint>? OnMessageStorm { get; set; }

    /// <summary>已处理的消息总数（用于阶段 0 性能诊断）。</summary>
    public static long MessageCount;

    /// <summary>最近处理的消息编号。</summary>
    public static uint LastMessage;

    /// <summary>连续相同消息的计数。</summary>
    public static int SameMessageStreak;

    /// <summary>创建并显示窗口，随后阻塞运行消息循环，直到收到 WM_QUIT。</summary>
    /// <remarks>窗口不设标题：作为工具窗口既无标题栏，也无标题用途。</remarks>
    public static void Run(int x, int y, int width, int height, IWindowMessageSink sink)
    {
        // Per-Monitor V2：多显示器与不同缩放比下布局不抖的前提
        WindowApi.SetProcessDpiAwarenessContext(WindowApi.DpiAwarenessContextPerMonitorV2);

        var host = new NativeWindowHost(sink);
        var hwnd = host.Create(x, y, width, height);

        host.Handle = (nint)hwnd.Value;

        // 由宿主显式触发初始化：WM_CREATE 早于 GWLP_USERDATA 写入，无法作为初始化时机
        sink.OnWindowCreated(host.Handle);

        // SW_SHOWNOACTIVATE：显示但不抢走当前前台窗口的焦点
        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        MSG msg;
        while (true)
        {
            // 必须显式比较返回值：GetMessage 返回 -1 表示出错，而 BOOL(-1) 在 C# 中为真，
            // 若写成 while (GetMessage(...)) 会把错误当成"还有消息"，导致消息泵 100% 空转。
            var result = PInvoke.GetMessage(&msg, HWND.Null, 0, 0).Value;

            if (result == 0)
            {
                break;
            }

            if (result == -1)
            {
                OnMessageLoopError?.Invoke(Marshal.GetLastWin32Error());
                break;
            }

            MessageCount++;

            // 消息类型直方图：用于区分"消息洪泛"与"GetMessage 错误空转"
            if (LastMessage == msg.message)
            {
                SameMessageStreak++;
            }
            else
            {
                LastMessage = msg.message;
                SameMessageStreak = 1;
            }

            if (SameMessageStreak > 100_000)
            {
                OnMessageStorm?.Invoke(msg.message);
                break;
            }

            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }

        host.Dispose();
    }

    /// <summary>把窗口消息转交给宿主注入的处理器。</summary>
    public nint HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
        => _sink.HandleMessage(hwnd, message, wParam, lParam);

    private HWND Create(int x, int y, int width, int height)
    {
        _selfHandle = GCHandle.Alloc(this);

        var hInstance = (HINSTANCE)WindowApi.GetModuleHandle(null);
        var cursor = (HCURSOR)PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW).Value;

        var wndClass = new WNDCLASSEXW
        {
            cbSize = WindowApi.WndClassExSize,
            style = WNDCLASS_STYLES.CS_OWNDC | WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
            lpfnWndProc = WndProcHandler.Handler,
            hInstance = hInstance,
            hCursor = cursor,
            hbrBackground = HBRUSH.Null,
            lpszClassName = (char*)ClassNameBuffer,
        };

        if (PInvoke.RegisterClassEx(wndClass) == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassEx 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }

        // WS_EX_TOOLWINDOW  : 不出现在 Alt+Tab
        // WS_EX_NOACTIVATE  : 点击不夺走当前窗口焦点
        // WS_EX_TOPMOST     : 置于 topmost 带（阶段 1 由防抢占策略持续重申）
        var exStyle = WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                      | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                      | WINDOW_EX_STYLE.WS_EX_TOPMOST;

        // 刻意不带 WS_VISIBLE：由 Run 在初始化完成后以 SW_SHOWNOACTIVATE 显示
        var style = WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_CLIPSIBLINGS;

        var hwnd = PInvoke.CreateWindowEx(
            exStyle,
            (char*)ClassNameBuffer,
            default(PCWSTR),
            style,
            x, y, width, height,
            HWND.Null,
            HMENU.Null,
            hInstance,
            null);

        if (hwnd.IsNull)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }

        WindowApi.SetWindowLongPtr(hwnd, WindowApi.GwlpUserData, GCHandle.ToIntPtr(_selfHandle));
        return hwnd;
    }

    private void Dispose()
    {
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    /// <summary>判断 DWM 合成是否可用；不可用时合成路径需要降级。</summary>
    public static bool IsCompositionEnabled()
        => WindowApi.DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
}
