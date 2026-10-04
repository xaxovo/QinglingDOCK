using System.Diagnostics;
using QinglingDock.Interop;
using QinglingDock.Rendering;

namespace QinglingDock.App;

/// <summary>
/// 阶段 0 宿主：验证渲染内核与生命周期，尚不包含图标布局。
/// 目标是把"提交即云端出包"的流水线打通，同时证明：
/// ① DirectComposition 合成链路可用；② 隐藏态不提交帧（GPU 归零）；
/// ③ 帧时间 P99 落在 60Hz 单帧预算内。
/// </summary>
internal sealed class DockApplication : IWindowMessageSink, IDisposable
{
    private const nuint VisibilityToggleTimerId = 1;
    private const int DockHeight = 64;
    private const int MinimumVisibleStrip = 4;
    private const uint VisibilityToggleIntervalMs = 4000;

    private CompositionRenderer? _renderer;
    private nint _hwnd;
    private bool _isDocked = true;
    private bool _rendererFailed;
    private int _toggleCount;
    private long _messageCount;
    private Timer? _cpuWatchdog;
    private readonly Stopwatch _watchdogClock = new();
    private TimeSpan _lastCpuTime;
    private TimeSpan _lastWatchdogWall;

    public void Run()
    {
        if (!NativeWindowHost.IsCompositionEnabled())
        {
            Log.Warn("DWM 合成不可用，DirectComposition 合成路径无法工作。");
        }

        NativeWindowHost.OnMessageLoopError = errorCode =>
            Log.Error($"消息泵异常退出，Win32 错误码 {errorCode}。");

        NativeWindowHost.OnMessageStorm = message =>
            Log.Error($"检测到消息洪泛，消息编号 0x{message:X}，已中止消息循环以避免 CPU 空转。");

        var bounds = CalculateBottomDockBounds();
        Log.Info($"停靠位置 x={bounds.X} y={bounds.Y} w={bounds.Width} h={bounds.Height}");

        NativeWindowHost.Run(bounds.X, bounds.Y, bounds.Width, bounds.Height, this);
    }

    /// <summary>窗口创建完成、宿主已就绪后建立与 HWND 绑定的渲染资源。</summary>
    public void OnWindowCreated(nint hwnd)
    {
        _hwnd = hwnd;

        try
        {
            var bounds = CalculateBottomDockBounds();

            // DirectComposition 目标需要有效 HWND，因此渲染资源在窗口创建后再建立
            _renderer = new CompositionRenderer(
                hwnd,
                bounds.Width,
                bounds.Height,
                logInfo: Log.Info,
                logWarning: Log.Warn,
                logError: Log.Error);

            Log.Info("渲染内核对象已建立，正在启动渲染线程……");
            _renderer.Start();

            Log.Info($"QinglingDOCK 阶段 0 已启动，窗口句柄=0x{hwnd:X}");

            // 阶段 0 自检：定时切换显隐，用于观察隐藏态的 GPU/CPU 是否归零。
            // 阶段 1 起改由鼠标边缘感应与防抢占策略驱动。
            //
            // QDOCK_DIAG 诊断开关（阶段 1 移除）：
            //   hidden  —— 全程不显示且不启动定时器，用于测量隐藏态真实开销
            //   visible —— 全程显示且不启动定时器，用于测量可见态真实开销
            //   其他/未设置 —— 默认每 4 秒切换一次显隐
            var diag = Environment.GetEnvironmentVariable("QDOCK_DIAG");

            if (string.Equals(diag, "hidden", StringComparison.OrdinalIgnoreCase))
            {
                _isDocked = false;
                _renderer.SetVisible(false);
                Log.Info("诊断模式 hidden：不启动定时器，全程不提交帧。");
            }
            else if (string.Equals(diag, "visible", StringComparison.OrdinalIgnoreCase))
            {
                _isDocked = true;
                Log.Info("诊断模式 visible：不启动定时器，持续提交帧。");
            }
            else
            {
                _isDocked = true;
                var timerId = WindowPlacement.StartTimer(hwnd, VisibilityToggleTimerId, VisibilityToggleIntervalMs);
                Log.Info($"显隐自检定时器 id={timerId}（每 {VisibilityToggleIntervalMs} ms 切换一次）");
            }

            StartCpuWatchdog();
        }
        catch (Exception ex)
        {
            _rendererFailed = true;
            Log.Error("初始化渲染内核失败。", ex);
            WindowPlacement.PostQuit(1);
        }
    }

    /// <summary>
    /// 进程级 CPU 采样。用于把"CPU 到底花在哪个线程/环节"这件事量化，
    /// 而不是靠猜测——阶段 0 必须自证性能预算（见 docs/PLAN.md §2.6）。
    /// </summary>
    private void StartCpuWatchdog()
    {
        _watchdogClock.Restart();
        _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;

        _cpuWatchdog = new Timer(_ =>
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var cpuNow = process.TotalProcessorTime;
                var wallNow = _watchdogClock.Elapsed;

                var cpuDelta = cpuNow - _lastCpuTime;
                var wallDelta = wallNow - _lastWatchdogWall;

                _lastCpuTime = cpuNow;
                _lastWatchdogWall = wallNow;

                if (wallDelta.TotalSeconds <= 0)
                {
                    return;
                }

                var cores = cpuDelta.TotalSeconds / wallDelta.TotalSeconds;
                Log.Info(
                    $"资源采样 单核占用={cores * 100:F0}% " +
                    $"工作集={process.WorkingSet64 / 1024 / 1024}MB " +
                    $"线程数={process.Threads.Count} " +
                    $"消息累计={NativeWindowHost.MessageCount} " +
                    $"最近消息=0x{NativeWindowHost.LastMessage:X} " +
                    $"连续同消息={NativeWindowHost.SameMessageStreak}");
            }
            catch (Exception ex)
            {
                Log.Warn($"资源采样失败：{ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6));
    }

    nint IWindowMessageSink.HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        _messageCount++;

        switch ((WindowMessage)message)
        {
            case WindowMessage.WM_PAINT:
                // 画面由 DirectComposition 合成，这里只需验证更新区域；
                // 否则 WM_PAINT 会无限重复入队导致消息泵空转。
                WindowPlacement.ValidatePaint(hwnd);
                return 0;

            case WindowMessage.WM_SIZE:
                var width = MessagePacking.LowWord(lParam);
                var height = MessagePacking.HighWord(lParam);
                if (width > 0 && height > 0)
                {
                    _renderer?.RequestResize(width, height);
                }

                return 0;

            case WindowMessage.WM_TIMER:
                if ((nuint)wParam == VisibilityToggleTimerId)
                {
                    ToggleVisibilityDemo();
                    return 0;
                }

                break;

            case WindowMessage.WM_DISPLAYCHANGE:
                OnDisplayChanged();
                return 0;

            case WindowMessage.WM_DESTROY:
                WindowPlacement.PostQuit(0);
                return 0;
        }

        return 0;
    }

    private void ToggleVisibilityDemo()
    {
        if (_hwnd == 0 || _renderer is null || _rendererFailed)
        {
            return;
        }

        _toggleCount++;
        _isDocked = !_isDocked;

        var bounds = CalculateBottomDockBounds();
        var y = _isDocked ? bounds.Y : bounds.Y + DockHeight - MinimumVisibleStrip;
        var height = _isDocked ? DockHeight : MinimumVisibleStrip;

        WindowPlacement.SetTopmostBounds(_hwnd, bounds.X, y, bounds.Width, height);

        // 隐藏态停止提交帧 → GPU 占用应归零；这是性能预算的关键一环
        _renderer.SetVisible(_isDocked);

        Log.Info(_isDocked
            ? $"Dock 展开（第 {_toggleCount} 次切换，开始提交帧）。"
            : $"Dock 隐藏（第 {_toggleCount} 次切换，停止提交帧，GPU 应为 0%）。");

        Log.Info($"累计处理窗口消息 {_messageCount} 条。");
    }

    private void OnDisplayChanged()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var bounds = CalculateBottomDockBounds();
        Log.Info($"显示器配置变化，重算停靠位置 x={bounds.X} y={bounds.Y} w={bounds.Width}");

        WindowPlacement.SetTopmostBounds(
            _hwnd,
            bounds.X,
            _isDocked ? bounds.Y : bounds.Y + DockHeight - MinimumVisibleStrip,
            bounds.Width,
            _isDocked ? DockHeight : MinimumVisibleStrip);
    }

    /// <summary>
    /// 计算主显示器底部停靠区（位于工作区之内，天然避开系统任务栏）。
    /// 阶段 2 扩展为每屏独立窗口与四边停靠。
    /// </summary>
    private static (int X, int Y, int Width, int Height) CalculateBottomDockBounds()
    {
        var work = MonitorApi.GetPrimaryWorkArea();
        return (work.Left, work.Bottom - DockHeight, work.Width, DockHeight);
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            WindowPlacement.StopTimer(_hwnd, VisibilityToggleTimerId);
        }

        _renderer?.Dispose();
    }
}
