# QinglingDOCK（清零 DOCK）

轻量、流畅、**不会被顶掉**的 Windows 桌面 Dock。

> **当前状态：阶段 0（工程骨架 + 渲染内核验证）**
> 已打通 Direct3D11 + DirectComposition 合成链路，性能指标实测达标。
> 图标布局、放大动画、应用固定等交互功能自阶段 1 开始实现。

---

## 为什么再造一个 Dock

现有的 Windows Dock 生态有两个明显缺口：

- **闭源或停更**：Winstep Nexus 是商业软件，RocketDock 停更十余年、源码缺失。
- **能覆盖但不可靠**：依靠 `WS_EX_TOPMOST` 的 Dock 会被任务栏、开始菜单或
  其他 topmost 工具挤到下面——而且被挤下去之后**仍然持有 topmost 标志**，
  `IsAlwaysOnTop` 依旧返回 true，只有无条件重新断言才能恢复。
- **功能齐全但太重**：Seelen-UI 走 WebView2 渲染（AGPL-3.0，内存 150MB+），
  对"常驻小工具"这个定位而言过重。

QinglingDOCK 的目标很窄：**只做 Dock，把流畅与资源占用做到位，并保证置顶优先级**。

---

## 性能实测（阶段 0，可复现）

测试环境：Windows 10 22H2 (19045) / AMD Radeon RX590 GME / 1920×1080

| 指标 | 实测值 | 目标 | 结论 |
|---|---|---|---|
| **隐藏态 CPU** | 0.07 – 0.20 % 单核 | ≈ 0（不提交帧） | ✅ |
| **可见态 CPU** | 3.77 % 单核 | ≤ 5 % | ✅ |
| **工作集** | 36 MB（隐藏） / 46.5 MB（可见） | < 60 MB | ✅ |
| **帧率** | 60.2 fps | ≥ 50 fps | ✅ |
| **帧间隔 P99** | 18.8 ms | ≤ 20 ms | ✅ |
| **窗口消息** | ~1.5 条/秒 | — | ✅ |

复现方式（Release 构建后运行）：

```powershell
dotnet build QinglingDock.sln -c Release
./scripts/measure-perf.ps1 -Configuration Release -Seconds 30
```

应用内置了资源采样与帧统计，日志会自行给出上述数据，无需外部工具。

---

## 架构与关键设计

```
QinglingDock.sln
├─ src/QinglingDock.App          进程入口、生命周期、日志（多级回退）、资源自检
├─ src/QinglingDock.Core         纯逻辑层（帧统计等），零 Win32 依赖，可跨平台单测
├─ src/QinglingDock.Interop      CsWin32 生成的 Win32/COM 绑定 + 窗口宿主 + 显示器查询
├─ src/QinglingDock.Rendering    D3D11 + DirectComposition + Direct2D 渲染内核
└─ tests/QinglingDock.Core.Tests xUnit
```

### 1. 不做逐像素透明（关键决策）

实测数据（4K / 集显，满帧运行）显示逐像素透明合成的代价高得惊人：

| 组合 | GPU 占用 | DWM 占用 |
|---|---|---|
| DirectComposition + 预乘 Alpha | 75 – 80 % | 65 – 70 % |
| DirectComposition + `AlphaMode.Ignore` | **5 %** | **0 – 1 %** |

因此 Dock 采用**不透明材质 + 圆角轮廓**，交换链使用 `AlphaMode.Ignore`，
配合"窗口严格贴合 Dock 尺寸"与"隐藏时不提交帧"，把开销压到最低档位。

### 2. 帧节奏必须显式限帧

组合交换链的 `Present(1, ...)` 在多数驱动下**不会阻塞等待垂直同步**——
实测可达 9000+ fps 并把单个核心跑满。渲染循环因此以绝对时间基准显式限帧。

### 3. 自绘窗口必须验证更新区域

使用 DirectComposition 提交画面时窗口没有绘制语义。若不在 `WM_PAINT` 中
调用 `BeginPaint`/`EndPaint` 验证更新区域，该区域会一直有效，
`WM_PAINT` 被无限重复入队——实测消息泵可达 **10 万条消息/秒**、CPU 100%。

### 4. 消息泵必须显式判错

`GetMessage` 出错返回 -1，而 `BOOL(-1)` 在 C# 中为真。
若写成 `while (GetMessage(...))`，错误会被当成"还有消息"，消息泵 100% 空转。

### 5. 置顶策略（阶段 1 实现）

覆盖 + 自动隐藏模式放弃了 AppBar 的 Shell 级保证，因此由三层策略兜住：
`WS_EX_TOPMOST` 断言 + `SetWinEventHook` 监听前台窗口变化即时重申 +
全屏应用检测主动让位。钩子仅在可见期间启用，隐藏态 CPU 为零。

---

## 快速开始

### 环境要求

- Windows 10 19041+ / Windows 11
- .NET SDK 8.0（开发与 CI 使用同一版本，保证可复现）
- 可选：GitHub CLI（用于发布流程）

### 构建与运行

```powershell
dotnet build QinglingDock.sln -c Release
dotnet test  QinglingDock.sln -c Release

# 运行（默认每 4 秒切换一次显隐，用于观察隐藏态开销）
./src/QinglingDock.App/bin/Release/net8.0-windows10.0.19041.0/QinglingDock.exe
```

### 诊断模式

通过环境变量 `QDOCK_DIAG` 可让 Dock 保持固定状态，便于测量：

```powershell
$env:QDOCK_DIAG = 'hidden'   # 全程不显示、不提交帧
$env:QDOCK_DIAG = 'visible'  # 全程显示、持续提交帧
```

### 日志位置

按以下顺序探测可写位置，全部失败时仅输出到调试器（不会静默丢弃）：

1. `%LOCALAPPDATA%\QinglingDOCK\logs\qinglingdocks.log`
2. 程序所在目录下的 `logs\qinglingdocks.log`
3. `%TEMP%\QinglingDOCK\logs\qinglingdocks.log`

---

## 版本管理与发布

- **版本号**：SemVer 2.0.0，唯一来源为 `Directory.Build.props` 的 `VersionPrefix`，
  CI 以 `-p:Version=<tag>` 覆盖。
- **提交规范**：Conventional Commits（`feat:` / `fix:` / `perf:` / `chore:`）。
- **分支模型**：`main`（受保护，仅经 PR 合入） ← `develop` ← `feature/*`；
  `release/vX.Y.Z` 准备发布，`vX.Y.Z` 标签触发正式发布。
- **云端编译**：全部由 GitHub Actions 在 `windows-latest` 上完成，
  本地无需安装额外工具链即可产出发布包。

---

## 文档

- 完整技术方案与选型依据：[docs/PLAN.md](docs/PLAN.md)

## 许可

[MIT](LICENSE)
