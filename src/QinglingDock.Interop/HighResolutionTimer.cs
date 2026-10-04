using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QinglingDock.Interop;

/// <summary>
/// 高精度可等待定时器，用于帧节奏控制。
///
/// 为什么不能直接用 Thread.Sleep：
/// 在未启用高精度定时器的进程/环境中，Thread.Sleep 会被系统时钟节拍（约 15.6ms）
/// 向上取整，导致周期性长帧。实测可见态帧间隔 P99 稳定在 31ms
/// （约 2 × 15.6ms），即约 1% 的帧睡过头。
/// 显式创建带 CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 的定时器可把粒度降到 1ms 以下，
/// 且等待期间线程完全挂起、不占用 CPU。
/// </summary>
public sealed class HighResolutionTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly SafeWaitHandle? _handle;
    private bool _disposed;

    public HighResolutionTimer()
    {
        // 仅 Windows 10 1803+ 支持 HIGH_RESOLUTION 标志；失败时退回普通定时器
        var handle = CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        }

        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            _handle = new SafeWaitHandle(handle, ownsHandle: true);
            IsHighResolution = true;
        }
    }

    /// <summary>是否成功创建定时器。为 false 时调用方应退回其他等待方式。</summary>
    public bool IsHighResolution { get; }

    /// <summary>
    /// 等待指定时长。返回 true 表示正常到期；false 表示定时器不可用或出错，
    /// 调用方可据此退回 Thread.Sleep。
    /// </summary>
    public bool Wait(TimeSpan duration)
    {
        if (_handle is null || _disposed || duration <= TimeSpan.Zero)
        {
            return false;
        }

        // 负值表示相对时间，单位为 100 纳秒
        var dueTime = -(long)(duration.TotalMilliseconds * 10_000);

        if (!SetWaitableTimer(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            return false;
        }

        var result = WaitForSingleObject(_handle, Infinite);

        return result == WaitObject0 || result == WaitTimeout;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle?.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        SafeWaitHandle hTimer,
        ref long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle hHandle, uint dwMilliseconds);
}
