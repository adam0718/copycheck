# DECISIONS.md — 技术决策记录

记录「为什么这样做」以及踩过的坑。换模型、隔几周回来接手时，先看这里。

---

## D-001 判据用剪贴板序列号，而不是按键

**日期**：2026-10-21

**决策**：用 `GetClipboardSequenceNumber()` 的变化判断复制是否成功，键盘钩子只用来知道「用户想复制」。

**理由**：监听 Ctrl+C 按键只能证明按键到达了系统，不能证明剪贴板被写入。远程桌面断连、程序禁止复制时，按键照样到达。如果用按键判成功，就会在最需要提示的时候撒谎——这比不提示更糟。

**验证**：`tests/verify_sequence.py` 4/4 通过。关键边界——重复写入完全相同的内容时序列号仍递增，因此「序列号没变」可以确定等于「剪贴板没被写过」。

---

## D-002 双阈值 + Pending 中间态，而不是单一超时

**日期**：2026-10-21

**决策**：本机 450ms、远程桌面 1200ms 报告「还没检测到更新」（Pending），2600ms 才最终判失败。

**理由**：RDP 剪贴板走重定向，写入延迟可达数百毫秒。若 450ms 就判失败，会出现「先说失败、200ms 后又说成功」的改口，用户会觉得软件不可靠。Pending 态让提示语从「失败了」变成「还在等」，既不撒谎也不让用户干等。

**实现**：Pending 之后若检测到序列号变化，原地把提示内容改成「已复制」并重新计时，窗口不重新淡入（避免闪烁）。

---

## D-003 终端窗口必须排除

**日期**：2026-10-21

**决策**：前台窗口是终端类程序时，跳过检测。

**理由**：终端里 Ctrl+C 是 SIGINT 中断信号，不写剪贴板。不排除的话，每次中断程序都会误报「复制失败」，一天几十次，用户会直接卸载。

**实现**：`GetClassName()` 匹配 `ConsoleWindowClass` / `CASCADIA_HOSTING_WINDOW_CLASS`，进程名匹配 cmd / powershell / pwsh / conhost / WindowsTerminal / mintty / bash / wsl 等。

**注意**：不能排除 VS Code（`Code`）。VS Code 里 Ctrl+C 是复制，只有终端面板聚焦时才是中断。按进程名排除会误伤。

---

## D-004 键盘钩子回调里绝不能做耗时操作

**日期**：2026-10-21

**决策**：钩子回调只做「读 vkCode + 查修饰键 + 记录前台窗口句柄」，然后 `SynchronizationContext.Post` 回 UI 线程处理。终端识别（要查进程）放在轮询 tick 里做。

**理由**：WH_KEYBOARD_LL 回调在安装线程的消息循环里同步执行，任何耗时操作都会直接卡住整个系统的键盘输入。`Process.GetProcessById()` 要几毫秒，放进去会让打字发涩。

**另一个必须注意的点**：`SetWindowsHookEx` 传入的委托必须用字段持有强引用，否则被 GC 回收后回调会跳到野地址导致进程崩溃。

---

## D-005 自绘 Form 必须设 AutoScaleMode.None

**日期**：2026-10-21（踩坑）

**症状**：代码算出窗口应该是 470px 宽，实际显示只有 247px，左半边被裁掉。

**原因**：Form 的 `AutoScaleMode` 默认是 `Font`。`Show()` 时 WinForms 会按字体比例执行 `PerformAutoScale`，把手工算好的 Size 二次缩放，而 `Region` 还是按原尺寸设的，于是窗口形状和内容对不上。

**修复**：`AutoScaleMode = AutoScaleMode.None`，尺寸完全手工控制。

---

## D-006 Region 不能挂在 OnResize 上

**日期**：2026-10-21（踩坑）

**症状**：程序启动后进程活着，但托盘没出现、钩子没装、trace 日志停在「配置已载入」。UI 线程被锁死。

**原因**：`OnResize` 里调用 `ApplyRegion()` 设置 `Region`，而设置 Region 会再触发一次 Resize，形成无限循环。构造函数里的 `new ToastForm()` 永远不返回。

**修复**：`ApplyRegion()` 只在 `RecalcLayout()` 末尾显式调用，不挂事件。

---

## D-007 环境：便携 SDK 而非 winget 安装

**日期**：2026-10-21

**决策**：下载 .NET 8 SDK 的 zip 解压到 `C:\Users\Adam\.dotnet`，用绝对路径调用 `dotnet.exe`。

**理由**：winget 安装需要 UAC 提权，会打断用户。便携版不需要管理员权限，不写系统目录，不用了直接删目录即可。

**顺带发现**：这台机器的安全策略禁止直接调用 `csc.exe`（等同于 Add-Type），但 `dotnet build` / `dotnet publish` 正常可用——因为走的是 Roslyn 的 `csc.dll` 而非 `csc.exe`。所以 C# 项目仍然能正常构建。

---

## D-008 目标框架改用 .NET Framework 4.8

**日期**：2026-10-21

**决策**：目标框架从 .NET 8 改为 **.NET Framework 4.8**，产物是 37KB 的单文件 exe。

**演进过程**（三次尝试，前两次都失败）：

| 方案 | 体积 | 结果 |
|---|---|---|
| .NET 8 自包含单文件 | 63MB | 被 Smart App Control 拦截，无法运行（见 D-011） |
| .NET 8 自包含目录 | 146MB / 242 文件 | 能运行，但体积和文件数对一个提示工具完全不合理 |
| **net48 单文件** | **37KB** | **能运行，启动 115ms** ← 采用 |

**顺带否掉的方案**：`.NET 8 + PublishTrimmed` 直接报 `NETSDK1175`——WinForms 明确不支持 IL 裁剪（大量依赖反射，裁剪必崩）。

**net48 改造清单**（供以后参考，都是 .NET Core 才有而 Framework 没有的 API）：

| 原 API | 替代 |
|---|---|
| `Math.Clamp` | 自写 `Num.Clamp`（见 `src/Num.cs`） |
| `s[..n]` / `s[^n..]` Range 语法 | `Substring` |
| `{ get; init; }` | `{ get; set; }` |
| `System.Text.Json` | 手写 `key=value` 的 settings.ini |
| `Environment.ProcessPath` | `Application.ExecutablePath` |
| `Application.SetHighDpiMode()` | app.manifest 里声明 `dpiAwareness` |
| `IntPtr == int` 比较 | `wParam == (IntPtr)WM_KEYDOWN` |
| `ImplicitUsings` | 关闭，各文件手动 using |

**意外收获**：启动时间从 838ms 降到 115ms，因为不用加载 63MB 的自解压包。

---

## D-011 Smart App Control 会拦掉 .NET 单文件自解压

**日期**：2026-10-21（踩坑，且是个重要发现）

**症状**：编译成功、build 目录的 exe 能跑，但 `dotnet publish` 出的单文件 exe 一启动就报
`OSError: [WinError 4551] 应用程序控制策略已阻止此文件`。

**诊断**：读注册表 `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy` 的 `VerifiedAndReputablePolicyState`，值为 `1` = **Smart App Control 处于强制模式**。同时 `System32\CodeIntegrity\CiPolicies\Active` 下有多份 WDAC 策略。

**原因**：.NET 单文件应用运行时会把自己解压到 `%TEMP%\.net\...` 再执行，这个「释放文件并执行」的行为在 SAC 眼里就是 dropper 特征。普通 PE（net48 产物）没有这个行为，所以放行。

**结论与影响**：

- 在这台机器上，**.NET 单文件自包含发布这条路永久不可用**，不管怎么调参数
- 目录发布（exe + dll）能绕过，但 146MB 不可接受
- net48 单文件是唯一同时满足「小、单文件、能运行」的方案
- 以后做 Windows 工具，如果目标机器开了 SAC，优先考虑 net48 或 C++ 而不是 .NET 8 自包含单文件

**验证方法**：`Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy'` 看 `VerifiedAndReputablePolicyState`（0=关闭 1=强制 2=评估）。

---

## D-009 单实例用 Mutex，不用命名管道

**日期**：2026-10-21

**决策**：`Mutex(@"Local\CopyCheck.SingleInstance.v1")`，重复启动时弹框提示已在运行。

**理由**：`Local\` 前缀限定在当前登录会话，多用户登录同一台机器时各自可以跑一份，不会互相干扰。功能简单，不值得引入 IPC。

---

## D-010 提示窗的交互约束

**日期**：2026-10-21

**决策**：`WS_EX_NOACTIVATE`（点击不激活）+ `WS_EX_TRANSPARENT`（鼠标穿透）+ `WS_EX_TOOLWINDOW`（不进 Alt+Tab）+ `TopMost`。

**理由**：提示窗的存在意义是「告知」，一旦抢焦点或挡住鼠标，就从提示变成了干扰。鼠标穿透尤其重要——提示窗出现在屏幕底部中央，正好是很多软件的工具栏位置。

**副作用**：如果屏幕上还有别的 TopMost 窗口（某些播放器、权限弹窗），提示窗可能被它盖住。这是 Windows 的 TopMost 层级决定的，v0.1 未处理。

---

## D-012 粘贴不做「成功判定」，只回答「粘的是什么」

**日期**：2026-10-22

**决策**：Ctrl+V 时立即显示剪贴板内容预览，但不声称「粘贴成功」，颜色用蓝色而非绿色。

**理由**：粘贴是目标程序的行为——它可能接受、可能忽略、可能转换格式（比如从 Word 粘到记事本会丢格式）。CopyCheck 站在系统外面，只能读到「剪贴板里现在是什么」，读不到「目标窗口收下了什么」。

如果硬要判成功，唯一的办法是 UI Automation 读取目标控件内容做前后对比——不可靠（大部分控件读不到）、昂贵（每次粘贴要遍历 UI 树）、误报率高（富文本编辑器会改写内容）。

所以选择诚实：**蓝色 + 「已粘贴」+ 内容预览**。这个提示的真实价值不是「确认成功」，而是「让你知道粘的是什么」。当粘贴结果不对时，能立刻分清是复制环节错了还是粘贴环节错了。

---

## D-013 右键复制靠被动监听，代价用静默策略抵消

**日期**：2026-10-22

**决策**：常驻监听剪贴板序列号，捕获没有键盘事件的写入（右键菜单复制、程序内复制按钮、拖放等），归为 `PassiveCopy`。

**理由**：右键菜单的「复制」不产生 Ctrl+C 按键，键盘钩子完全看不到。唯一可行的手段是监听剪贴板本身。

**代价**：分不清「用户主动复制」和「别的程序偷偷写剪贴板」。典型骚扰源是密码管理器（复制密码后自动清空剪贴板）、部分同步工具。

**抵消策略**：

- 被动路径遇到**空内容**直接静默（密码管理器清空剪贴板是最高频的骚扰源）
- 被动路径遇到**内容与上次相同**也静默（用户没按键，弹「未变化」纯属噪音）
- 提供「检测右键复制」开关，默认开，嫌吵可以关

**已知无法区分**：右键菜单的「剪切」写入的剪贴板格式与复制相同，会显示成「已复制」。要区分得解析 `Preferred DropEffect` 数据格式（`DROPEFFECT_MOVE`），但只有部分程序会设置，不可靠。

---

## D-014 空内容必须和成功分开报

**日期**：2026-10-22（端到端测试中发现）

**症状**：在没选中任何文字的窗口里按 Ctrl+C，程序会往剪贴板写入**空文本**。序列号确实变了，于是 CopyCheck 报绿色「已复制」——**这正是在报假成功**，而这个工具存在的全部意义就是不打假报告。

**根因**：`ClipboardSnapshot` 把「有文本格式但长度为 0」归类为 `ClipKind.Text`（预览显示「（空文本）」），所以 `Kind == Empty` 的判断不成立。

**修复**：两层

1. `CaptureOnce` 里空文本的 `Kind` 改为 `ClipKind.Empty`——对用户来说「有文本格式但内容为空」和「剪贴板是空的」没有区别，都是没东西可粘
2. 新增 `CopyOutcome.EmptyContent`，提示「复制的内容是空的」，琥珀色 + 感叹号

**顺带修正**：`ResolvePassive` 遇到空内容直接静默，避免密码管理器清空剪贴板时弹提示。

**教训**：判断「成功」时，序列号变化只是必要条件，不是充分条件——**还得看写进去的是不是有实际内容**。
