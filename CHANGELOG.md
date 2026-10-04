# Changelog

本文件记录 QinglingDOCK 的所有重要变更。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本 2.0.0](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 计划中（阶段 1）
- 图标布局引擎与鼠标放大的弹性动画
- 已运行窗口指示与点击激活
- 应用固定 / 取消固定、拖拽排序
- 自动隐藏的边缘感应交互
- 置顶防抢占策略（前台窗口事件驱动重申 + 全屏让位）

### 计划中（工程优化）
- 自包含单文件产物启用裁剪与 ReadyToRun，压缩分发体积
  （当前自包含 ZIP 37.7 MB / 解压后 94 MB，其中大部分为 .NET 运行时）

## [0.1.1] - 2026-10-04

阶段 0 的帧节奏修正与工程收尾。

### 新增
- `HighResolutionTimer`：基于 `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` 的
  高精度可等待定时器，用于帧节奏控制

### 性能
- 帧间隔 P99 从 31 ms 降至 **17.6 ms**，可见态 CPU 从 3.77% 降至 **1.37%** 单核。
  `Thread.Sleep` 在部分环境受系统时钟节拍（约 15.6 ms）向上取整造成周期性长帧，
  改用高精度定时器后等待期间线程完全挂起

### 文档
- README 补齐实测性能数据与分发体积对照
- 记录阶段 0 发现并修复的 6 类易错点，纳入 CONTRIBUTING 供后续开发规避

## [0.1.0] - 2026-10-04

阶段 0：工程骨架、云端流水线与渲染内核验证。

### 新增
- 解决方案分层：`App` / `Core` / `Interop` / `Rendering` 与 `Core.Tests`
- Direct3D11 + DirectComposition + Direct2D 渲染内核，独立渲染线程
- 原生 Win32 窗口宿主（Per-Monitor V2 DPI、`WS_EX_NOACTIVATE`、`WS_EX_TOOLWINDOW`）
- 渲染循环显式限帧，帧率锁定 60fps
- 帧率与帧间隔统计（含 P99），用于自证性能预算
- 进程级资源采样（单核占用、工作集、线程数、消息计数）
- 日志多级回退与写入探测，失败可见而非静默丢弃
- `QDOCK_DIAG` 诊断开关，用于隔离测量隐藏态 / 可见态开销
- 版本号单一来源（`Directory.Build.props`）与集中包版本管理
- 性能验收脚本 `scripts/measure-perf.ps1`
- 单元测试覆盖帧统计逻辑

### 修复
- 窗口过程在 `GWLP_USERDATA` 尚未写入时的空句柄崩溃
- `GetMessage` 返回 -1 被误判为"还有消息"导致消息泵 100% 空转
- 未验证更新区域导致 `WM_PAINT` 无限重复入队（实测 10 万条消息/秒）
- `ManualResetEvent` 不复位使帧节流退化为纯自旋，单核占用 100%
- 组合交换链 `Present` 不阻塞垂直同步导致的数千 fps 空转
- 日志写入失败被静默吞掉，导致启动问题无从定位

[Unreleased]: https://github.com/xaxovo/QinglingDOCK/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/xaxovo/QinglingDOCK/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/xaxovo/QinglingDOCK/releases/tag/v0.1.0
