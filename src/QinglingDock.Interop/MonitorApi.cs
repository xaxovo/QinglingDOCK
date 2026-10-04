using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace QinglingDock.Interop;

/// <summary>屏幕工作区（已排除系统任务栏所占区域），单位为物理像素。</summary>
public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// 显示器与窗口位置的公共 API 层。
/// CsWin32 生成的类型（HWND / HMONITOR 等）为 internal，不能出现在公共签名中，
/// 因此由本类型收敛原生类型，对外只暴露托管类型。
/// 阶段 2 将在此扩展每屏枚举与四边停靠所需接口。
/// </summary>
public static class MonitorApi
{
    /// <summary>取主显示器的工作区。查询失败时回退为 1920x1080，保证进程仍可启动。</summary>
    public static WorkArea GetPrimaryWorkArea()
    {
        var primary = PInvoke.MonitorFromWindow(HWND.Null, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

        var info = new MONITORINFO { cbSize = MonitorInfoSize };
        if (!PInvoke.GetMonitorInfo(primary, ref info))
        {
            return new WorkArea(0, 0, 1920, 1080);
        }

        var work = info.rcWork;
        return new WorkArea(work.left, work.top, work.right, work.bottom);
    }

    /// <summary>
    /// MONITORINFO 的字节大小：cbSize(4) + rcMonitor(16) + rcWork(16) + dwFlags(4) = 40。
    /// 该值必须精确，否则 GetMonitorInfo 会失败。
    /// </summary>
    private const uint MonitorInfoSize = 40;
}

/// <summary>窗口位置与定时器的托管封装，隔离 CsWin32 内部类型。</summary>
public static unsafe class WindowPlacement
{
    /// <summary>HWND_TOPMOST 的哨兵值 (HWND)(-1)。</summary>
    private static readonly HWND HwndTopmost = unchecked((HWND)(nint)(-1));

    /// <summary>以不激活的方式移动并显示窗口，同时重申置顶。</summary>
    public static void SetTopmostBounds(nint hwnd, int x, int y, int width, int height)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
                    | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW
                    | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

        PInvoke.SetWindowPos(new HWND(hwnd), HwndTopmost, x, y, width, height, flags);
    }

    /// <summary>启动周期性定时器，返回定时器标识（0 表示失败）。</summary>
    public static nuint StartTimer(nint hwnd, nuint id, uint intervalMilliseconds)
        => WindowApi.SetTimer(new HWND(hwnd), id, intervalMilliseconds, 0);

    /// <summary>销毁定时器。</summary>
    public static void StopTimer(nint hwnd, nuint id)
        => WindowApi.KillTimer(new HWND(hwnd), id);

    /// <summary>请求退出消息循环。</summary>
    public static void PostQuit(int exitCode) => PInvoke.PostQuitMessage(exitCode);

    /// <summary>
    /// 完成一次窗口绘制周期并<b>验证更新区域</b>。
    ///
    /// 这一步是必需的，不是可选优化：我们用 DirectComposition 提交画面，
    /// 窗口自身没有绘制语义，若不在 WM_PAINT 中验证更新区域，
    /// 该更新区域会一直有效，WM_PAINT 被无限重复入队 —— 消息泵将 100% 空转
    /// （实测可达 10 万条消息/秒）。BeginPaint/EndPaint 正是清除该区域的正确方式。
    /// </summary>
    public static void ValidatePaint(nint hwnd)
    {
        var ps = new PAINTSTRUCT();
        var hdc = PInvoke.BeginPaint(new HWND(hwnd), &ps);
        PInvoke.EndPaint(new HWND(hwnd), &ps);
        _ = hdc;
    }
}
