# CHANGELOG

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.1.0] - 2026-10-21

首个可用版本。

### 新增

- 全局键盘钩子捕获 Ctrl+C（自动排除 Ctrl+Shift+C 与 Ctrl+Alt+C）
- 基于 `GetClipboardSequenceNumber()` 的复制结果验证，区分三种结果：
  - **已复制**：剪贴板确实更新，显示内容前 34 字 + 字数
  - **内容未变化**：剪贴板写入但内容与上次相同
  - **未复制成功**：超过扩展窗口仍无更新，附可能原因
- Pending 中间态：首个阈值到达时先提示「还没检测到更新」，继续等待，检测到变化后原地改口
- 远程桌面会话自动检测，阈值从 450ms 放宽到 1200ms
- 终端窗口排除（cmd / PowerShell / Windows Terminal / mintty / WSL 等）
- 提示窗：圆角深色卡片、状态色竖条、几何图形图标、1.5s 停留后淡出
- 提示窗不抢焦点（WS_EX_NOACTIVATE）、鼠标穿透（WS_EX_TRANSPARENT）、不进 Alt+Tab（WS_EX_TOOLWINDOW）
- 多显示器支持：出现在鼠标所在屏幕
- 托盘菜单：试一下提示效果 / 跳过终端窗口 / 开机自启 / 关于 / 退出
- 单实例保护（Mutex）
- 未捕获异常写入 `%APPDATA%\CopyCheck\error.log`
- 配置持久化到 `%APPDATA%\CopyCheck\settings.json`
- 诊断日志：设置 `COPYCHECK_TRACE=1` 后写入 `trace.log`

### 变更

- 目标框架从 .NET 8 改为 **.NET Framework 4.8**：产物从 146MB 目录降到 **37KB 单文件**，启动时间从 838ms 降到 115ms
- 配置格式从 `settings.json` 改为 `settings.ini`（net48 下不引入 JSON 库）

### 修复

- 自绘窗口被 WinForms 自动缩放导致尺寸错误、左侧内容被裁切（`AutoScaleMode = None`）
- `OnResize` 中设置 `Region` 触发无限递归，导致启动时 UI 线程死锁

### 已知限制

- 需要 .NET Framework 4.8（Windows 10 1809+ / Windows 11 系统自带，无需安装）
- 管理员权限程序的按键无法捕获（UIPI 限制）
- 其他 TopMost 窗口可能遮挡提示窗
- 仅支持 x64，Windows 10 1607 及以上

### 验证

- `tests/verify_sequence.py`：剪贴板序列号判据 4/4 通过
- `tests/e2e_test.py`：三条提示路径端到端通过
- `tests/capture_toast.py`：三种提示窗渲染截图确认
