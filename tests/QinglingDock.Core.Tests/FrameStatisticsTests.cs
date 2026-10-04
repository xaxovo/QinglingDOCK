using QinglingDock.Core;
using Xunit;

namespace QinglingDock.Core.Tests;

public sealed class FrameStatisticsTests
{
    private const double Tolerance = 0.001;

    [Fact]
    public void Constructor_RejectsTooSmallWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameStatistics(15));
    }

    [Fact]
    public void EmptyStatistics_ReportZero()
    {
        var stats = new FrameStatistics();

        Assert.Equal(0, stats.SampleCount);
        Assert.Equal(0, stats.SmoothedFps);
        Assert.Equal(0, stats.MaxFrameTimeMs);
        Assert.Equal(0, stats.GetPercentileFrameTimeMs(0.99));
    }

    [Fact]
    public void Record_RejectsNegativeFrameTimeByClamping()
    {
        var stats = new FrameStatistics();

        stats.Record(-5);

        Assert.Equal(1, stats.SampleCount);
        Assert.Equal(0, stats.MaxFrameTimeMs);
    }

    [Fact]
    public void Record_FirstSampleAdoptsInstantFpsWithoutRampUp()
    {
        var stats = new FrameStatistics();

        stats.Record(16.6667);

        Assert.Equal(60.0, stats.SmoothedFps, 0.01);
    }

    [Fact]
    public void Record_SmoothsSubsequentSamples()
    {
        var stats = new FrameStatistics();
        stats.Record(16.6667); // ≈60fps

        stats.Record(33.3333); // ≈30fps

        // 平滑值 = 60*0.9 + 30*0.1 = 57
        Assert.Equal(57.0, stats.SmoothedFps, 0.05);
    }

    [Fact]
    public void Record_KeepsOnlyWindowSizedSamples()
    {
        var stats = new FrameStatistics(16);

        for (var i = 0; i < 100; i++)
        {
            stats.Record(10);
        }

        Assert.Equal(16, stats.SampleCount);
    }

    [Fact]
    public void Percentile_ReturnsWorstCaseAtP100()
    {
        var stats = new FrameStatistics(100);

        for (var i = 0; i < 99; i++)
        {
            stats.Record(5);
        }

        stats.Record(50);

        Assert.Equal(50, stats.GetPercentileFrameTimeMs(1.0), Tolerance);
    }

    [Fact]
    public void Percentile_P99StaysWithinBudgetWhenOnlyTwoSlowFrames()
    {
        var stats = new FrameStatistics(100);

        for (var i = 0; i < 98; i++)
        {
            stats.Record(8);
        }

        stats.Record(30);
        stats.Record(40);

        // 100 帧中排序后 P99 落在 30ms，超出 16.67ms 预算 —— 必须能被检出
        Assert.True(
            stats.GetPercentileFrameTimeMs(0.99) > FrameStatistics.FrameBudget60HzMilliseconds,
            "存在 2 帧超预算时 P99 应超过单帧预算，性能回归必须可被发现。");
    }

    [Fact]
    public void Percentile_RejectsOutOfRangeInput()
    {
        var stats = new FrameStatistics();
        stats.Record(16);

        Assert.Throws<ArgumentOutOfRangeException>(() => stats.GetPercentileFrameTimeMs(1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => stats.GetPercentileFrameTimeMs(-0.1));
    }

    [Fact]
    public void MaxFrameTimeMs_TracksWorstFrameEverRecorded()
    {
        var stats = new FrameStatistics();

        stats.Record(10);
        stats.Record(45.5);
        stats.Record(12);

        Assert.Equal(45.5, stats.MaxFrameTimeMs, Tolerance);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var stats = new FrameStatistics();
        stats.Record(100);
        stats.Record(100);

        stats.Reset();

        Assert.Equal(0, stats.SampleCount);
        Assert.Equal(0, stats.SmoothedFps);
        Assert.Equal(0, stats.MaxFrameTimeMs);
        Assert.Equal(0, stats.GetPercentileFrameTimeMs(0.99));
    }

    [Fact]
    public void FrameBudget_CorrespondsTo60Hz()
    {
        Assert.Equal(16.6667, FrameStatistics.FrameBudget60HzMilliseconds, 0.001);
    }
}
