# 阶段 0 性能验收脚本
# 用法: pwsh -File scripts/measure-perf.ps1 [-Seconds 30] [-Configuration Release]
param(
    [int]$Seconds = 30,
    [string]$Configuration = 'Release',
    [int]$StartupWarmupSeconds = 6
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exeDir = Join-Path $root "src\QinglingDock.App\bin\$Configuration\net8.0-windows10.0.19041.0"
$exe = Join-Path $exeDir 'QinglingDock.exe'

if (-not (Test-Path $exe)) {
    throw "未找到可执行文件：$exe`n请先执行：dotnet build QinglingDock.sln -c $Configuration"
}

$logDir = Join-Path $exeDir 'logs'
if (Test-Path $logDir) { Remove-Item $logDir -Recurse -Force }

$outFile = Join-Path $env:TEMP 'qinglingdocks-perf-out.txt'
$errFile = Join-Path $env:TEMP 'qinglingdocks-perf-err.txt'
Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue

Write-Host "启动 $exe"
$process = Start-Process -FilePath $exe -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile

try {
    Start-Sleep -Seconds $StartupWarmupSeconds

    $process.Refresh()
    $cpuStart = $process.TotalProcessorTime
    $wsStart = $process.WorkingSet64
    $wallStart = Get-Date

    Write-Host "预热完成，开始 $Seconds 秒稳态采样……"
    Start-Sleep -Seconds $Seconds

    $process.Refresh()
    $cpuEnd = $process.TotalProcessorTime
    $wsEnd = $process.WorkingSet64
    $wallEnd = Get-Date

    $wallSeconds = ($wallEnd - $wallStart).TotalSeconds
    $cpuSeconds = ($cpuEnd - $cpuStart).TotalSeconds
    $cores = [math]::Round($cpuSeconds / $wallSeconds, 3)

    Write-Host ''
    Write-Host '================ 稳态测量结果 ================'
    Write-Host ("采样时长          : {0:N1} s" -f $wallSeconds)
    Write-Host ("进程 CPU 时间     : {0:N2} s" -f $cpuSeconds)
    Write-Host ("单核占用          : {0:N1} %" -f ($cores * 100))
    Write-Host ("工作集 起/止      : {0:N1} MB / {1:N1} MB" -f ($wsStart / 1MB), ($wsEnd / 1MB))
    Write-Host ("线程数            : {0}" -f $process.Threads.Count)
    Write-Host '============================================='
    Write-Host ''

    # 稳态判据：空闲（自动隐藏占半程）下应远低于 1 个核心
    if ($cores -lt 0.25) {
        Write-Host "[通过] 单核占用 < 25%" -ForegroundColor Green
    } else {
        Write-Host "[未通过] 单核占用 >= 25%，需要继续优化" -ForegroundColor Red
    }

    if (($wsEnd / 1MB) -lt 80) {
        Write-Host ("[通过] 工作集 {0:N1} MB < 80 MB" -f ($wsEnd / 1MB)) -ForegroundColor Green
    } else {
        Write-Host ("[未通过] 工作集 {0:N1} MB >= 80 MB" -f ($wsEnd / 1MB)) -ForegroundColor Red
    }
}
finally {
    if (-not $process.HasExited) {
        taskkill /PID $process.Id /F 2>&1 | Out-Null
    }

    $log = Join-Path $logDir 'qinglingdocks.log'
    if (Test-Path $log) {
        Write-Host ''
        Write-Host '================ 应用日志（尾部 20 行）================'
        Get-Content $log -Encoding UTF8 -Tail 20
    }
    else {
        Write-Host '未找到应用日志。'
    }
}
