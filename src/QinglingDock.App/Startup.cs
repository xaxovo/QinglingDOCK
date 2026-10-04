namespace QinglingDock.App;

/// <summary>
/// 进程入口。使用原生 Win32 消息循环而非 <c>Application.Run</c>，
/// 因为置顶策略与每屏 DPI 需要直接控制窗口消息（见 docs/PLAN.md §2.4）。
/// </summary>
internal static class Startup
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    private static int Main()
    {
        // 日志必须在最早期就绪：常驻进程的启动失败若无日志将无从定位
        var logStatus = Log.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("未处理异常。", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("未观察的任务异常。", e.Exception);
            e.SetObserved();
        };

        // 单实例：多个实例会造成多个 Dock 窗口互相争抢置顶
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            @"Local\QinglingDOCK.SingleInstance",
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            Log.Warn("检测到已有实例在运行，本次启动退出。");
            return 0;
        }

        var version = typeof(Startup).Assembly.GetName().Version?.ToString() ?? "unknown";
        Log.Info($"===== QinglingDOCK 启动，程序集版本 {version} =====");
        Log.Info($"日志位置={logStatus.Path} 回退={logStatus.UsedFallback}");

        foreach (var probeError in logStatus.ProbeErrors)
        {
            Log.Warn($"日志候选位置不可用：{probeError}");
        }

        try
        {
            using var app = new DockApplication();
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Error("主循环异常退出。", ex);
            return 1;
        }
        finally
        {
            Log.Info("===== QinglingDOCK 退出 =====");
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }

        return 0;
    }
}
