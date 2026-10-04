namespace QinglingDock.Core;

/// <summary>
/// 帧率与帧时间统计。用于在运行时自证性能预算（见 docs/PLAN.md §2.6：
/// 交互帧率 ≥ 60fps，帧时间 P99 &lt; 16.7ms），无需外部工具即可回归验证。
/// 仅在渲染线程上访问，因此不加锁。
/// </summary>
public sealed class FrameStatistics
{
    /// <summary>60Hz 下的单帧间隔（16.67ms），作为帧节奏的参考基准。</summary>
    public const double FrameBudget60HzMilliseconds = 1000.0 / 60.0;

    /// <summary>
    /// 帧间隔的验收上限。
    /// 60Hz 下实际帧间隔必然略大于 16.67ms（垂直同步取样误差 + 定时器抖动），
    /// 因此不能用 16.67ms 作为判据——那会把正常表现误判为掉帧。
    /// 这里以 50fps（20ms）作为"不卡顿"的下限门槛。
    /// </summary>
    public const double MaximumAcceptableFrameIntervalMs = 1000.0 / 50.0;

    private readonly double[] _recentFrameTimesMs;
    private readonly double[] _scratch;
    private int _count;
    private int _next;
    private double _smoothedFps;

    public FrameStatistics(int windowSize = 240)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 16);
        _recentFrameTimesMs = new double[windowSize];
        _scratch = new double[windowSize];
    }

    /// <summary>窗口内已记录的帧数（上限为构造时指定的窗口大小）。</summary>
    public int SampleCount => _count;

    /// <summary>指数平滑后的帧率，用于界面显示，避免数字剧烈跳动。</summary>
    public double SmoothedFps => _smoothedFps;

    /// <summary>窗口内最高的单帧耗时（毫秒），用于捕捉最差表现。</summary>
    public double MaxFrameTimeMs { get; private set; }

    /// <summary>
    /// 记录一帧。
    /// </summary>
    /// <param name="frameTimeMs">
    /// 应为<b>帧间隔</b>（上一帧呈现到本帧呈现），而不是单帧的绘制耗时。
    /// 帧间隔才反映实际帧率与卡顿；绘制耗时单独统计更有意义。
    /// </param>
    public void Record(double frameTimeMs)
    {
        if (frameTimeMs < 0)
        {
            frameTimeMs = 0;
        }

        _recentFrameTimesMs[_next] = frameTimeMs;
        _next = (_next + 1) % _recentFrameTimesMs.Length;

        if (_count < _recentFrameTimesMs.Length)
        {
            _count++;
        }

        if (frameTimeMs > MaxFrameTimeMs)
        {
            MaxFrameTimeMs = frameTimeMs;
        }

        var instantFps = frameTimeMs > 0 ? 1000.0 / frameTimeMs : 0;

        // 首个样本直接采用，避免从 0 缓慢爬升造成的读数失真
        _smoothedFps = _smoothedFps <= 0
            ? instantFps
            : (_smoothedFps * 0.9) + (instantFps * 0.1);
    }

    /// <summary>计算给定分位的帧时间（毫秒）。用于验证 P99 是否落在单帧预算内。</summary>
    public double GetPercentileFrameTimeMs(double percentile)
    {
        if (percentile is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "分位值必须位于 [0, 1]。");
        }

        if (_count == 0)
        {
            return 0;
        }

        Array.Copy(_recentFrameTimesMs, _scratch, _count);
        var slice = _scratch.AsSpan(0, _count);
        slice.Sort();

        // 最近邻取整：P99 在 240 帧窗口下对应第 237 个样本
        var index = (int)Math.Round((_count - 1) * percentile, MidpointRounding.ToZero);
        return slice[Math.Clamp(index, 0, _count - 1)];
    }

    /// <summary>清空统计，用于重新开始一段测量。</summary>
    public void Reset()
    {
        Array.Clear(_recentFrameTimesMs);
        _count = 0;
        _next = 0;
        _smoothedFps = 0;
        MaxFrameTimeMs = 0;
    }
}
