using System.Diagnostics;
using QinglingDock.Interop;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;
using static Vortice.Direct3D11.D3D11;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using D2DRenderTargetType = Vortice.Direct2D1.RenderTargetType;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace QinglingDock.Rendering;

/// <summary>
/// 渲染内核：D3D11 设备 + DirectComposition 交换链 + Direct2D 绘制，由独立渲染线程驱动。
///
/// 关键设计（依据见 docs/PLAN.md §2.1 的实测数据）：
/// 交换链使用 <see cref="Vortice.DXGI.AlphaMode.Ignore"/> 而非 Premultiplied。
/// 逐像素透明合成会让 DWM 付出十几倍代价（实测 GPU 75-80% / DWM 65-70%，
/// 而 Ignore 档位仅 5% / 0-1%）。因此 Dock 采用不透明材质 + 圆角轮廓，
/// 不做逐像素 alpha，靠"窗口严格贴合 Dock 尺寸 + 隐藏时不提交帧"把开销压到最低。
/// </summary>
public sealed class CompositionRenderer : IDisposable
{
    private const int FrameCount = 2;
    private const Format ColorFormat = Format.B8G8R8A8_UNorm;

    /// <summary>目标帧间隔（60Hz）。渲染循环按此节流，避免 composer 不阻塞时空转烧 CPU。</summary>
    private static readonly TimeSpan TargetFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    private readonly nint _hwnd;
    private readonly object _gate = new();
    private readonly Action<string>? _logInfo;
    private readonly Action<string>? _logWarning;
    private readonly Action<string, Exception?>? _logError;

    private Thread? _renderThread;
    private volatile bool _disposed;

    // —— 渲染线程独占状态 ——
    private ID3D11Device1? _device;
    private ID3D11DeviceContext1? _deviceContext;
    private IDXGISwapChain1? _swapChain;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1RenderTarget? _renderTarget;
    private ID2D1SolidColorBrush? _panelBrush;
    private ID2D1SolidColorBrush? _accentBrush;
    private int _width;
    private int _height;
    private bool _resizePending;

    // —— 调度状态（跨线程共享）——
    private readonly ManualResetEventSlim _renderSignal = new(false);
    private readonly Stopwatch _frameTimer = new();
    private readonly Stopwatch _paceClock = new();
    private readonly HighResolutionTimer _paceTimer = new();
    private TimeSpan _totalThrottleWait;
    private TimeSpan _totalDrawCost;
    private long _totalPresentCalls;
    private volatile bool _visible = true;

    /// <summary>
    /// 构造渲染内核。
    /// 日志通过回调注入，使渲染层不反向依赖上层（分层单向依赖）。
    /// </summary>
    public CompositionRenderer(
        nint hwnd,
        int width,
        int height,
        Action<string>? logInfo = null,
        Action<string>? logWarning = null,
        Action<string, Exception?>? logError = null)
    {
        _hwnd = hwnd;
        _width = width;
        _height = height;
        _logInfo = logInfo;
        _logWarning = logWarning;
        _logError = logError;
    }

    /// <summary>帧率与帧时间统计，供上层读取以自证性能预算。</summary>
    public Core.FrameStatistics Statistics { get; } = new();

    /// <summary>渲染内核是否已成功建立（可用于启动自检）。</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>启动独立渲染线程。</summary>
    public void Start()
    {
        if (_renderThread is not null)
        {
            throw new InvalidOperationException("渲染线程已启动。");
        }

        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "QinglingDock.Render",
            Priority = ThreadPriority.AboveNormal,
        };
        _renderThread.Start();
    }

    /// <summary>窗口尺寸变化时由界面线程调用，实际重建在渲染线程内完成。</summary>
    public void RequestResize(int width, int height)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            _resizePending = true;
        }

        Wake();
    }

    /// <summary>显隐状态变化。隐藏时渲染线程挂起，GPU 占用归零。</summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        Wake();
    }

    /// <summary>唤醒渲染线程提交一帧（用于输入驱动的重绘）。</summary>
    public void Wake() => _renderSignal.Set();

    private void RenderLoop()
    {
        _logInfo?.Invoke("渲染线程已启动。");

        try
        {
            InitializeDevice();
            IsInitialized = true;
            _logInfo?.Invoke("渲染内核初始化完成，进入渲染循环。");

            var statsReportTimer = Stopwatch.StartNew();
            _paceClock.Restart();
            var nextFrameAt = _paceClock.Elapsed + TargetFrameInterval;

            while (!_disposed)
            {
                if (!_visible)
                {
                    // 隐藏态：不提交任何帧，等待唤醒信号（自动隐藏下的绝大多数时间）
                    _renderSignal.Wait(TimeSpan.FromMilliseconds(250));
                    _renderSignal.Reset();
                    nextFrameAt = _paceClock.Elapsed + TargetFrameInterval;
                    continue;
                }

                _frameTimer.Restart();

                ApplyPendingResize();

                var drawStart = _paceClock.Elapsed;
                DrawFrame();
                _totalDrawCost += _paceClock.Elapsed - drawStart;

                if (statsReportTimer.ElapsedMilliseconds >= 2000)
                {
                    statsReportTimer.Restart();
                    ReportPerformance();
                }

                // 组合交换链（composition swap chain）的 Present 在多数驱动下不会阻塞等待
                // 垂直同步，实测可达数千 fps 并把单核跑满；因此必须以绝对时间基准显式限帧。
                var waitStart = _paceClock.Elapsed;
                WaitUntil(nextFrameAt);
                _totalThrottleWait += _paceClock.Elapsed - waitStart;
                _totalPresentCalls++;

                // 同步到显示器垂直同步
                _swapChain!.Present(1, PresentFlags.None);

                // 记录真实帧间隔（应以 16.67ms 为基准，而不是单帧绘制耗时）
                Statistics.Record(_frameTimer.Elapsed.TotalMilliseconds);

                // 调度下一帧时刻；若已落后超过一个周期则重新对齐，避免补帧风暴
                nextFrameAt += TargetFrameInterval;
                if (_paceClock.Elapsed - nextFrameAt > TargetFrameInterval)
                {
                    nextFrameAt = _paceClock.Elapsed + TargetFrameInterval;
                }
            }
        }
        catch (Exception ex)
        {
            // 渲染线程的任何异常都必须留下痕迹，否则表现为"界面不更新但进程还在"
            _logError?.Invoke("渲染线程异常终止。", ex);
        }
    }

    /// <summary>
    /// 等待到指定时刻。
    ///
    /// 这里必须使用 <see cref="AutoResetEvent"/> 而不是 ManualResetEvent：
    /// ManualResetEvent 在被 Set 之后不会自动复位，会使 WaitOne 从第二次起立即返回，
    /// 让本方法退化成纯自旋（实测把单个核心跑满），而且自旋耗时还会被误计入"等待时间"，
    /// 使性能统计看起来正常 —— 属于极难察觉的错误。
    ///
    /// 实现选择：优先使用高精度可等待定时器，退回 Thread.Sleep。
    ///
    /// Thread.Sleep 在部分环境下受系统时钟节拍（约 15.6ms）向上取整，
    /// 会造成周期性长帧（实测帧间隔 P99 稳定在 31ms ≈ 2 × 15.6ms）。
    /// <see cref="HighResolutionTimer"/> 使用 CREATE_WAITABLE_TIMER_HIGH_RESOLUTION，
    /// 粒度低于 1ms，且等待期间线程完全挂起、不占用 CPU。
    /// </summary>
    private void WaitUntil(TimeSpan deadline)
    {
        while (true)
        {
            var remaining = deadline - _paceClock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            // 留出少量余量，避免睡过头造成长帧
            var wait = remaining - TimeSpan.FromMilliseconds(0.8);
            if (wait <= TimeSpan.Zero)
            {
                Thread.SpinWait(100);
                return;
            }

            if (!_paceTimer.Wait(wait))
            {
                // 定时器不可用（系统过旧或被策略限制）时退回 Sleep
                Thread.Sleep(wait);
                return;
            }
        }
    }

    private void ReportPerformance()
    {
        var stats = Statistics;
        if (stats.SampleCount < 30)
        {
            return;
        }

        var p99 = stats.GetPercentileFrameTimeMs(0.99);
        var withinBudget = p99 <= Core.FrameStatistics.MaximumAcceptableFrameIntervalMs;

        var presents = _totalPresentCalls;
        var waitMs = _totalThrottleWait.TotalMilliseconds;
        var drawMs = _totalDrawCost.TotalMilliseconds;
        _totalPresentCalls = 0;
        _totalThrottleWait = TimeSpan.Zero;
        _totalDrawCost = TimeSpan.Zero;

        _logInfo?.Invoke(
            $"渲染统计 fps={stats.SmoothedFps:F1} p99间隔={p99:F2}ms max={stats.MaxFrameTimeMs:F2}ms " +
            $"达标={(withinBudget ? "是" : "否")} 帧数={presents} 绘制耗时={drawMs:F0}ms " +
            $"节流等待={waitMs:F0}ms");
    }

    private void ApplyPendingResize()
    {
        bool pending;
        int targetWidth;
        int targetHeight;

        lock (_gate)
        {
            pending = _resizePending;
            targetWidth = _width;
            targetHeight = _height;
            _resizePending = false;
        }

        if (!pending || _swapChain is null || targetWidth <= 0 || targetHeight <= 0)
        {
            return;
        }

        // 必须先释放所有引用后备缓冲的对象，否则 ResizeBuffers 会失败
        _renderTarget?.Dispose();
        _renderTarget = null;

        _swapChain.ResizeBuffers(FrameCount, (uint)targetWidth, (uint)targetHeight, ColorFormat);
        CreateRenderTarget();
    }

    private void InitializeDevice()
    {
        using var dxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        _logInfo?.Invoke("DXGI 工厂已创建。");

        using var adapter = FindHardwareAdapter(dxgiFactory)
            ?? throw new InvalidOperationException("未找到可用的硬件显示适配器。");
        _logInfo?.Invoke($"已选定适配器：{adapter.Description1.Description.Trim()}");

        // Level_11_1 需要 Windows 8+ 且驱动支持；DirectComposition 实际只需 11_0。
        // 仍由高到低尝试，末尾以 Unspecified 兜底。
        var featureLevels = new D3DFeatureLevel[]
        {
            D3DFeatureLevel.Level_11_1,
            D3DFeatureLevel.Level_11_0,
            D3DFeatureLevel.Level_10_1,
            D3DFeatureLevel.Level_10_0,
            D3DFeatureLevel.Level_9_3,
            D3DFeatureLevel.Level_9_2,
            D3DFeatureLevel.Level_9_1,
        };

        var result = D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device? device,
            out ID3D11DeviceContext? deviceContext);

        if (result.Failure || device is null || deviceContext is null)
        {
            throw new InvalidOperationException($"D3D11CreateDevice 失败：{result.Description}");
        }

        _logInfo?.Invoke($"D3D11 设备已创建，适配器={adapter.Description1.Description.Trim()}");

        // 使用 ID3D11Device1 / ID3D11DeviceContext1（DirectComposition 需要）
        _device = device.QueryInterface<ID3D11Device1>();
        _deviceContext = deviceContext.QueryInterface<ID3D11DeviceContext1>();
        device.Dispose();
        deviceContext.Dispose();

        // FlipSequential + CreateSwapChainForComposition 是 DirectComposition 合成的必要组合
        var swapChainDescription = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = ColorFormat,
            BufferCount = FrameCount,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = SampleDescription.Default,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        _swapChain = dxgiFactory.CreateSwapChainForComposition(_device, swapChainDescription);
        _logInfo?.Invoke($"组合交换链已创建：{_width}x{_height} {ColorFormat} FlipSequential AlphaIgnore");

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

        if (_compositionDevice.CreateTargetForHwnd(_hwnd, topmost: true, out var target).Failure)
        {
            throw new InvalidOperationException("CreateTargetForHwnd 失败：无法为窗口建立合成目标。");
        }

        _compositionTarget = target;
        _compositionVisual = _compositionDevice.CreateVisual();
        _compositionVisual.SetContent(_swapChain);
        _compositionTarget.SetRoot(_compositionVisual);
        _compositionDevice.Commit();
        _logInfo?.Invoke("DirectComposition 目标与视觉树已提交");

        _d2dFactory = D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        CreateRenderTarget();

        _logInfo?.Invoke(
            $"渲染内核就绪：适配器={adapter.Description1.Description.Trim()} " +
            $"尺寸={_width}x{_height} 交换链=FlipSequential/{ColorFormat} 合成=DirectComposition");
    }

    private static IDXGIAdapter1? FindHardwareAdapter(IDXGIFactory2 factory)
    {
        using var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        if (factory6 is not null)
        {
            for (uint index = 0; ; index++)
            {
                if (factory6.EnumAdapterByGpuPreference(
                        index, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Failure)
                {
                    break;
                }

                if (adapter is null)
                {
                    continue;
                }

                if ((adapter.Description1.Flags & AdapterFlags.Software) == AdapterFlags.None)
                {
                    return adapter;
                }

                adapter.Dispose();
            }
        }

        for (uint index = 0; ; index++)
        {
            if (factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Failure)
            {
                break;
            }

            if (adapter is null)
            {
                continue;
            }

            if ((adapter.Description1.Flags & AdapterFlags.Software) == AdapterFlags.None)
            {
                return adapter;
            }

            adapter.Dispose();
        }

        return null;
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        using var surface = backBuffer.QueryInterface<IDXGISurface>();

        var properties = new RenderTargetProperties
        {
            PixelFormat = new PixelFormat(ColorFormat, D2DAlphaMode.Ignore),
            Type = D2DRenderTargetType.Hardware,
        };

        _renderTarget = _d2dFactory!.CreateDxgiSurfaceRenderTarget(surface, properties);

        // D2D 以 DIP 为单位，绑定 96dpi 后按逻辑坐标绘制（DPI 缩放在布局层统一处理）
        _renderTarget.SetDpi(96, 96);

        _panelBrush = _renderTarget.CreateSolidColorBrush(new Color4(0.12f, 0.15f, 0.22f, 1.0f));
        _accentBrush = _renderTarget.CreateSolidColorBrush(new Color4(0.29f, 0.56f, 0.98f, 1.0f));
    }

    /// <summary>
    /// 阶段 0 的占位绘制：圆角不透明面板 + 动态进度条。
    /// 阶段 1 起替换为图标布局与放大动画的绘制实现。
    /// </summary>
    private void DrawFrame()
    {
        if (_renderTarget is null || _panelBrush is null || _accentBrush is null)
        {
            return;
        }

        const float inset = 1.0f;
        var radius = (_height / 2.0f) - inset;

        _renderTarget.BeginDraw();
        _renderTarget.Clear(new Color4(0, 0, 0, 0));

        var panel = new RoundedRectangle
        {
            Rect = new Rect(inset, inset, _width - (inset * 2), _height - (inset * 2)),
            RadiusX = radius,
            RadiusY = radius,
        };

        _renderTarget.FillRoundedRectangle(panel, _panelBrush);

        // 动态元素：便于肉眼确认渲染在持续推进，并暴露掉帧
        var progress = (float)(_frameTimer.Elapsed.TotalMilliseconds % 2000 / 2000.0);
        var barWidth = MathF.Max(8.0f, (_width - 24.0f) * progress);
        var bar = new RoundedRectangle
        {
            Rect = new Rect(12.0f, _height - 10.0f, barWidth, 4.0f),
            RadiusX = 2.0f,
            RadiusY = 2.0f,
        };

        _renderTarget.FillRoundedRectangle(bar, _accentBrush);
        _renderTarget.EndDraw();
    }

    public void Dispose()
    {
        _disposed = true;
        Wake();

        _renderThread?.Join(TimeSpan.FromSeconds(2));

        _panelBrush?.Dispose();
        _accentBrush?.Dispose();
        _renderTarget?.Dispose();
        _d2dFactory?.Dispose();
        _compositionVisual?.Dispose();
        _compositionTarget?.Dispose();
        _compositionDevice?.Dispose();
        _swapChain?.Dispose();
        _deviceContext?.Dispose();
        _device?.Dispose();
        _paceTimer.Dispose();
        _renderSignal.Dispose();
    }
}
