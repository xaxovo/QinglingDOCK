using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace QinglingDock.App;

/// <summary>日志落地结果，供启动日志与界面展示。</summary>
/// <param name="Path">实际使用的日志文件路径。</param>
/// <param name="UsedFallback">是否回退到了备用位置（主位置不可写）。</param>
/// <param name="ProbeErrors">探测各候选位置时出现的错误，用于诊断。</param>
public sealed record LogStatus(string Path, bool UsedFallback, IReadOnlyList<string> ProbeErrors);

/// <summary>
/// 极简文件日志。
///
/// 设计要点：
/// 1. 多候选位置回退 —— 优先 %LOCALAPPDATA%，不可写时退到程序所在目录，
///    仍不可写再退到临时目录。任何一环失败都会被记录，绝不静默丢弃日志。
/// 2. 每行即时 flush —— 常驻进程崩溃时最后的日志必须已经落盘，否则无从定位。
/// 3. 写入失败只上报一次 —— 避免磁盘满等异常在渲染线程里刷屏。
/// </summary>
public static class Log
{
    private const string FileName = "qinglingdocks.log";
    private const int MaxProbeErrors = 5;

    private static readonly object Gate = new();
    private static readonly List<string> ProbeFailures = new(MaxProbeErrors);
    private static string? _logFilePath;
    private static bool _usedFallback;
    private static bool _writeFailureReported;

    /// <summary>已就绪的日志路径；未调用 <see cref="Initialize"/> 时为 null。</summary>
    public static string? LogFilePath => _logFilePath;

    /// <summary>
    /// 探测并锁定一个可写的日志位置。应在最早时机调用一次。
    /// </summary>
    public static LogStatus Initialize()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (TryPrepare(candidate, out var error))
            {
                _logFilePath = Path.Combine(candidate, FileName);
                _usedFallback = !IsPreferred(candidate);
                return new LogStatus(_logFilePath, _usedFallback, ProbeFailures.ToArray());
            }

            if (ProbeFailures.Count < MaxProbeErrors && error is not null)
            {
                ProbeFailures.Add($"{candidate} -> {error}");
            }
        }

        return new LogStatus(string.Empty, UsedFallback: true, ProbeFailures.ToArray());
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        var builder = new StringBuilder(message);
        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        Write("ERROR", builder.ToString());
    }

    private static string[] EnumerateCandidates()
    {
        var candidates = new List<string>(3);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "QinglingDOCK", "logs"));
        }

        var exeDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(exeDirectory))
        {
            candidates.Add(Path.Combine(exeDirectory, "logs"));
        }

        var temp = Path.GetTempPath();
        if (!string.IsNullOrWhiteSpace(temp))
        {
            candidates.Add(Path.Combine(temp, "QinglingDOCK", "logs"));
        }

        return candidates.ToArray();
    }

    private static bool IsPreferred(string directory)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return !string.IsNullOrWhiteSpace(localAppData) &&
               directory.StartsWith(Path.Combine(localAppData, "QinglingDOCK"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>尝试建立目录并实际写入一次，确认真正可写（仅判断目录存在是不够的）。</summary>
    private static bool TryPrepare(string directory, out string? error)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(directory);

            // 真实写入探测：某些环境允许建目录但拒绝写文件
            var probeFile = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void Write(string level, string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");

        Debug.WriteLine(line);

        var path = _logFilePath;
        if (string.IsNullOrEmpty(path))
        {
            // 未初始化或全部候选位置都不可写：至少让调试器/控制台能拿到
            if (!_writeFailureReported)
            {
                _writeFailureReported = true;
                Debug.WriteLine("[ERROR] 日志位置不可用，后续日志仅输出到调试器。");
            }

            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_writeFailureReported)
            {
                _writeFailureReported = true;
                Debug.WriteLine($"[ERROR] 写入日志失败（{path}）：{ex.Message}");
            }
        }
    }
}
