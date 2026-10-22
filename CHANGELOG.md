# CHANGELOG

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.2.0] - 2026-10-22

### 新增

- **剪切检测（Ctrl+X）**：与复制共用序列号判据，提示「已剪切」
- **粘贴检测（Ctrl+V）**：读剪贴板显示粘贴内容，用蓝色与复制区分——外部无法确认粘贴是否成功，所以不声称成功
- **右键菜单复制检测**：常驻监听剪贴板序列号变化，捕获没有快捷键的复制动作（右键菜单、程序内复制按钮等）
- **空内容识别**：Ctrl+C 没选中内容时提示「复制的内容是空的」（琥珀色），不再把它报成绿色成功
- 托盘菜单「试一下提示效果」改为子菜单，可逐个预览六种状态
- 托盘菜单新增「粘贴时提示」「检测右键复制」两个开关
- 新增 `--demo=copy|cut|paste|unchanged|empty|failed` 命令行参数，用于预览效果和自动化测试

### 变更

- 空文本（有文本格式但长度为 0）归类为「剪贴板为空」，与真正的空剪贴板同等对待
- 被动复制（无按键写入）遇到空内容或重复内容时静默，避免密码管理器等程序写剪贴板造成骚扰
- 粘贴提示停留时间缩短到 1.1 秒（粘贴是高频操作，提示不宜久留）
- 版本号 0.1.0 → 0.2.0

### 修复

- 演示模式不参与单实例检查、不装键盘钩子，避免测试时的 Mutex 冲突
- 演示模式的显示动作改用 `SynchronizationContext.Post` 排队，修复 `Timer` 在消息循环启动前不触发的问题

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
