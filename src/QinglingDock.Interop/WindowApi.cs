using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace QinglingDock.Interop;

/// <summary>
/// 补充 Win32 声明。以下 API 因在 Win32 元数据中以宏或非可映射类型形式存在，
/// CsWin32 无法直接生成，因此在此手写（签名与 winuser.h / dwmapi.h 一致）。
/// </summary>
internal static class WindowApi
{
    private const string User32 = "user32.dll";
    private const string DwmApi = "dwmapi.dll";

    internal const int GwlExStyle = -20;
    internal const int GwlpUserData = -21;

    /// <summary>
    /// WNDCLASSEXW 的字节大小，用于 cbSize 字段。
    /// CsWin32 生成的 WNDCLASSEXW 在启用 AOT 分析时被视为托管类型，无法使用 sizeof，
    /// 故按字段自然对齐手工推导：
    ///   cbSize(4) + style(4) + lpfnWndProc(ptr) + cbClsExtra(4) + cbWndExtra(4)
    ///   + hInstance(ptr) + hIcon(ptr) + hCursor(ptr) + hbrBackground(ptr)
    ///   + lpszMenuName(ptr) + lpszClassName(ptr) + hIconSm(ptr)
    /// x64 下对齐后为 80，x86 下为 48。cbSize 填错会导致 RegisterClassEx 失败（错误码非 0），
    /// 因此该值由启动自检间接覆盖。
    /// </summary>
    internal static uint WndClassExSize => IntPtr.Size == 8 ? 80u : 48u;

    /// <summary>
    /// 读取窗口的长指针属性。<paramref name="index"/> 使用 <see cref="GwlExStyle"/>/<see cref="GwlpUserData"/>。
    /// 64 位下等价于 GetWindowLongPtrW。
    /// </summary>
    [DllImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(HWND hWnd, int index);

    /// <summary>写入窗口的长指针属性。64 位下等价于 SetWindowLongPtrW。</summary>
    [DllImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(HWND hWnd, int index, nint value);

    /// <summary>定时器。用 SetTimer 驱动阶段 0 的显隐自检，阶段 1 起由输入事件取代。</summary>
    [DllImport(User32, SetLastError = true)]
    internal static extern nuint SetTimer(HWND hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    /// <summary>销毁定时器。</summary>
    [DllImport(User32, SetLastError = true)]
    internal static extern bool KillTimer(HWND hWnd, nuint uIDEvent);

    /// <summary>声明进程 DPI 感知级别；失败通常意味着清单已声明。</summary>
    [DllImport(User32, SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(nint value);

    /// <summary>Per-Monitor V2 DPI 感知上下文句柄（Win10 1703+）。</summary>
    internal static readonly nint DpiAwarenessContextPerMonitorV2 = unchecked((nint)(-4));

    /// <summary>查询 DWM 合成是否启用。</summary>
    [DllImport(DwmApi)]
    internal static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    /// <summary>
    /// 取模块句柄。CsWin32 生成的 GetModuleHandle 返回 FreeLibrarySafeHandle，
    /// 不便直接放入 WNDCLASSEXW，故在此使用返回原生句柄的声明。
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? lpModuleName);
}
