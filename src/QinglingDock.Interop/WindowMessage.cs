namespace QinglingDock.Interop;

/// <summary>
/// 窗口消息常量。
/// Win32 元数据把 WM_* 表示为宏而非可映射类型，CsWin32 无法生成枚举，故在此手工声明
/// （数值与 winuser.h 完全一致，且保持稳定 ABI，不会随 SDK 变化）。
/// </summary>
public enum WindowMessage : uint
{
    WM_NULL = 0x0000,
    WM_CREATE = 0x0001,
    WM_DESTROY = 0x0002,
    WM_SIZE = 0x0005,
    WM_PAINT = 0x000F,
    WM_CLOSE = 0x0010,
    WM_QUIT = 0x0012,
    WM_ERASEBKGND = 0x0014,
    WM_SHOWWINDOW = 0x0018,
    WM_SETTINGCHANGE = 0x001A,
    WM_ACTIVATEAPP = 0x001C,
    WM_DISPLAYCHANGE = 0x007E,
    WM_TIMER = 0x0113,
    WM_NCPAINT = 0x0085,
    WM_MOUSEMOVE = 0x0200,
    WM_LBUTTONDOWN = 0x0201,
    WM_LBUTTONUP = 0x0202,
    WM_RBUTTONUP = 0x0205,
    WM_MOUSEWHEEL = 0x020A,
    WM_MOUSELEAVE = 0x02A3,
    WM_DPICHANGED = 0x02E0,
}

/// <summary>窗口消息参数的解析辅助。</summary>
public static class MessagePacking
{
    /// <summary>取低 16 位（作为有符号数），通常是鼠标 X 或窗口宽度。</summary>
    public static int LowWord(nint value) => (short)(value & 0xFFFF);

    /// <summary>取高 16 位（作为有符号数），通常是鼠标 Y 或窗口高度。</summary>
    public static int HighWord(nint value) => (short)((value >> 16) & 0xFFFF);
}
