# 贡献指南

## 环境准备

- Windows 10 19041+ / Windows 11
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
dotnet build QinglingDock.sln -c Release
dotnet test  QinglingDock.sln -c Release
```

## 分支模型

| 分支 | 用途 |
|---|---|
| `main` | 受保护，仅经 PR 合入，任何提交都应可构建 |
| `develop` | 集成分支 |
| `feature/*`、`fix/*` | 短生命周期工作分支 |
| `release/vX.Y.Z` | 发布准备 |

## 提交规范

使用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/)，
类型决定变更日志的分组：

```
feat:     新功能
fix:      缺陷修复
perf:     性能改进
refactor: 重构（不改变外部行为）
docs:     文档
test:     测试
build:    构建系统或依赖
ci:       CI 配置
chore:    其他杂项
```

示例：

```
feat(rendering): 支持图标弹性放大动画
fix(interop): 修复 WM_PAINT 未验证更新区域导致的消息泵空转
perf(render): 隐藏态停止提交帧，GPU 占用归零
```

## 版本号

SemVer 2.0.0。**唯一来源**是 `Directory.Build.props` 中的 `VersionPrefix`，
请勿在各项目文件里单独定义版本。CI 发布时会以 `-p:Version=<tag>` 覆盖。

## 代码约定

- 分层单向依赖：`App` → `Rendering` → `Interop`/`Core`。
  `Core` 为纯逻辑层，**不得**引入任何 Win32 依赖，以便跨平台单测。
- Win32 绑定统一通过 CsWin32 生成；需要新 API 时在
  `src/QinglingDock.Interop/NativeMethods.txt` 追加函数名，
  无法映射的类型（宏、`WM_*` 常量等）在 `WindowApi.cs` 手写声明。
- 构建启用 `TreatWarningsAsErrors`，请确保本地构建零警告。

## 性能要求

任何影响渲染路径的改动都必须自查性能。项目内置了资源采样与帧统计，
也可使用验收脚本：

```powershell
./scripts/measure-perf.ps1 -Configuration Release -Seconds 30
```

门槛（见 [README](README.md#性能实测阶段-0可复现)）：

| 指标 | 上限 |
|---|---|
| 隐藏态 CPU | ≈ 0% 单核 |
| 可见态 CPU | ≤ 5% 单核 |
| 工作集 | < 60 MB |
| 帧率 | ≥ 50 fps |
| 帧间隔 P99 | ≤ 20 ms |

若因环境限制无法运行脚本，请在 PR 描述中说明，并附上日志中的
`资源采样` 与 `渲染统计` 行。

## 已知易错点

这些是在阶段 0 中实际踩到并修复的坑，改动相关代码时请特别注意：

1. **`WM_PAINT` 必须验证更新区域**。用 DirectComposition 提交画面时窗口没有绘制
   语义，不调用 `BeginPaint`/`EndPaint` 会让 `WM_PAINT` 无限重复入队。
2. **`GetMessage` 返回 -1 是错误**，而 `BOOL(-1)` 在 C# 中为真。必须显式比较返回值。
3. **不要用 `ManualResetEvent` 做帧节流**，它不会自动复位，会让等待退化为自旋。
4. **组合交换链的 `Present` 不保证阻塞垂直同步**，必须显式限帧。
5. **`GWLP_USERDATA` 在 `WM_CREATE` 时尚未写入**，不要在那里做宿主初始化。
6. **窗口过程委托必须持有强引用**，否则被 GC 回收会导致进程崩溃。

## 报告问题

请附上日志（`%LOCALAPPDATA%\QinglingDOCK\logs\qinglingdocks.log`）以及
`资源采样` / `渲染统计` 行。若无法定位，用 `QDOCK_DIAG=hidden` 或
`QDOCK_DIAG=visible` 固定状态后复现，可有效区分"渲染开销"与"其他开销"。
