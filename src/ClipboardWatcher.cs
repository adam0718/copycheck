using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CopyCheck;

/// <summary>用户做了哪个动作。决定提示文案和颜色。</summary>
internal enum KeyAction
{
    /// <summary>Ctrl+C。</summary>
    Copy,

    /// <summary>Ctrl+X。剪切同样会写剪贴板，判据与复制一致。</summary>
    Cut,

    /// <summary>Ctrl+V。粘贴只读剪贴板，序列号不会变，因此不做「是否成功」判定。</summary>
    Paste,

    /// <summary>没有按键、但剪贴板被写了：右键菜单复制、程序内复制按钮、拖放等。</summary>
    PassiveCopy
}

internal enum CopyOutcome
{
    /// <summary>剪贴板确实被写入了，且有实际内容。</summary>
    Success,

    /// <summary>
    /// 剪贴板被写入了，但内容是空的。
    /// 典型场景：没有选中任何文字就按了 Ctrl+C，程序往剪贴板写了空数据。
    /// 这种情况必须和「复制成功」区分开，否则就是在报假成功。
    /// </summary>
    EmptyContent,

    /// <summary>剪贴板被写入了，但内容与上一次完全相同。</summary>
    Unchanged,

    /// <summary>超过首个阈值仍未见更新，但仍在继续等待（远程桌面下常见）。不是最终结论。</summary>
    Pending,

    /// <summary>等到扩展窗口结束仍无更新，判定操作失败。</summary>
    Failed
}

internal sealed class CopyResult
{
    public KeyAction Action { get; set; }
    public CopyOutcome Outcome { get; set; }
    public ClipboardSnapshot? Snapshot { get; set; }
    public double ElapsedMs { get; set; }
    public string? Hint { get; set; }
    public bool IsRemoteSession { get; set; }
}

/// <summary>
/// 复制 / 剪切 / 粘贴检测引擎。
///
/// 三套判据，因为三个动作的机制根本不同：
///
///   复制、剪切 —— 会写剪贴板。判据是「剪贴板序列号有没有变」。
///                 按键只说明用户想做，序列号才说明真的做了。
///
///   粘贴       —— 只读剪贴板，序列号不会变。无法从外部确认目标程序是否接收，
///                 所以只回答「你粘的是这个内容」，不声称「粘贴成功」。
///
///   右键复制   —— 没有按键事件，靠常驻监听序列号变化捕获。
///                 代价是分不清「用户主动复制」和「别的程序偷偷写剪贴板」。
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    // ---------- 可调参数 ----------
    /// <summary>本机判定「可能没成功」的等待阈值。</summary>
    public int LocalThresholdMs { get; set; } = 450;

    /// <summary>远程桌面下剪贴板重定向较慢，阈值放宽，避免误报后又要改口。</summary>
    public int RemoteThresholdMs { get; set; } = 1200;

    /// <summary>超过这个时间仍无更新，才最终判定失败。</summary>
    public int ExtendedWindowMs { get; set; } = 2600;

    /// <summary>是否跳过终端类窗口的 Ctrl+C（终端里它是 SIGINT 中断信号，不是复制）。</summary>
    public bool ExcludeTerminals { get; set; } = true;

    /// <summary>是否检测没有按键的剪贴板写入（右键菜单复制等）。</summary>
    public bool DetectPassiveCopy { get; set; } = true;

    /// <summary>是否在 Ctrl+V 时提示。</summary>
    public bool ShowPasteToast { get; set; } = true;

    /// <summary>去抖窗口，避免按住键产生的重复触发。</summary>
    private const int DebounceMs = 250;

    /// <summary>粘贴提示的节流：连按 Ctrl+V 时不要每次都弹。</summary>
    private const int PasteThrottleMs = 400;

    private const int PollIntervalMs = 45;

    // ---------- 事件 ----------
    /// <summary>检测结果（含 Pending 中间态）。始终在 UI 线程触发。</summary>
    public event Action<CopyResult>? OutcomeReported;

    // ---------- 状态 ----------
    private readonly bool _isRemote = NativeMethods.IsRemoteSession();
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly SynchronizationContext? _ctx;
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;   // 必须保留引用，否则会被 GC 回收导致崩溃
    private IntPtr _hookHandle = IntPtr.Zero;

    private bool _armed;
    private bool _terminalChecked;
    private bool _reportedPending;
    private KeyAction _pendingAction = KeyAction.Copy;
    private uint _seqAtKeypress;
    private uint _lastSeenSeq;
    private IntPtr _foregroundAtKeypress;
    private long _startTimestamp;
    private long _lastTriggerTimestamp;
    private long _lastPasteTimestamp;
    private string? _lastSignature;

    public bool IsRemoteSession => _isRemote;

    public ClipboardWatcher()
    {
        _ctx = SynchronizationContext.Current;
        _hookProc = HookCallback;

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += OnPollTick;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;

        // 低级键盘钩子：只用来知道「用户想做什么」，不做任何判定
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Trace.Log($"HOOK 安装失败 err={err}");
            throw new InvalidOperationException(
                $"键盘钩子安装失败（错误码 {err}）。可能被安全软件拦截。");
        }

        // 记下启动时的序列号，否则第一次 tick 会把「当前状态」误当成「刚刚被写入」
        _lastSeenSeq = NativeMethods.GetClipboardSequenceNumber();

        Trace.Log($"HOOK 安装成功，remote={_isRemote}, ctx={(_ctx == null ? "null" : "ok")}, 序列号基准={_lastSeenSeq}");
        _pollTimer.Start();
    }

    public void Stop()
    {
        _pollTimer.Stop();
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    // ---------- 钩子回调：必须极快返回，绝不能在这里做耗时操作 ----------
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 &&
            (wParam == (IntPtr)NativeMethods.WM_KEYDOWN ||
             wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)info.vkCode;

            KeyAction? action = null;
            if (vk == NativeMethods.VK_C) action = KeyAction.Copy;
            else if (vk == NativeMethods.VK_X) action = KeyAction.Cut;
            else if (vk == NativeMethods.VK_V) action = KeyAction.Paste;

            if (action.HasValue && IsPlainCtrl())
            {
                // 只记录事实，处理交给消息循环，避免阻塞键盘输入
                IntPtr fg = NativeMethods.GetForegroundWindow();
                KeyAction a = action.Value;
                Trace.Log($"捕获 Ctrl+{(a == KeyAction.Copy ? "C" : a == KeyAction.Cut ? "X" : "V")}，前台窗口 0x{fg.ToInt64():X}");

                if (_ctx != null)
                    _ctx.Post(_ => HandleHotkey(a, fg), null);
                else
                    HandleHotkey(a, fg);
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>要求「只按了 Ctrl」：带 Shift 的 Ctrl+Shift+C 在浏览器/IDE 里通常不是复制。</summary>
    private static bool IsPlainCtrl()
    {
        static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        return Down(NativeMethods.VK_CONTROL)
            && !Down(NativeMethods.VK_SHIFT)
            && !Down(NativeMethods.VK_MENU);
    }

    private void HandleHotkey(KeyAction action, IntPtr foreground)
    {
        if (action == KeyAction.Paste)
        {
            HandlePaste();
            return;
        }

        BeginTracking(action, foreground);
    }

    // ---------- 粘贴：立即给结果，不等剪贴板变化 ----------
    private void HandlePaste()
    {
        if (!ShowPasteToast) return;

        long now = Stopwatch.GetTimestamp();
        if ((now - _lastPasteTimestamp) * 1000.0 / Stopwatch.Frequency < PasteThrottleMs)
            return;

        _lastPasteTimestamp = now;

        var snapshot = ClipboardSnapshot.Capture();

        Trace.Log($"PASTE 剪贴板={snapshot.Kind} preview=\"{snapshot.Preview}\"");

        if (snapshot.IsEmpty)
        {
            OutcomeReported?.Invoke(new CopyResult
            {
                Action = KeyAction.Paste,
                Outcome = CopyOutcome.EmptyContent,
                Snapshot = snapshot,
                IsRemoteSession = _isRemote,
                Hint = "剪贴板是空的，没有可粘贴的内容"
            });
            return;
        }

        OutcomeReported?.Invoke(new CopyResult
        {
            Action = KeyAction.Paste,
            Outcome = CopyOutcome.Success,
            Snapshot = snapshot,
            IsRemoteSession = _isRemote
        });
    }

    // ---------- 开始跟踪一次复制 / 剪切 ----------
    private void BeginTracking(KeyAction action, IntPtr foreground)
    {
        long now = Stopwatch.GetTimestamp();

        // 按住不放会连续触发 KEYDOWN，用时间窗口去抖
        if (_armed && ElapsedMs(now) < DebounceMs)
            return;

        _pendingAction = action;
        _foregroundAtKeypress = foreground;
        _seqAtKeypress = NativeMethods.GetClipboardSequenceNumber();
        _startTimestamp = now;
        _lastTriggerTimestamp = now;
        _armed = true;
        _terminalChecked = false;
        _reportedPending = false;

        Trace.Log($"ARM {action} 序列号基准={_seqAtKeypress}");
    }

    // ---------- 轮询 ----------
    private void OnPollTick(object? sender, EventArgs e)
    {
        uint seqNow = NativeMethods.GetClipboardSequenceNumber();

        if (_armed)
        {
            // 终端判断放在这里做，避免在键盘钩子里查进程阻塞输入
            if (!_terminalChecked)
            {
                _terminalChecked = true;
                // 只有 Ctrl+C 需要排除终端（SIGINT）。Ctrl+X 在终端里没有特殊含义，照常检测。
                if (_pendingAction == KeyAction.Copy &&
                    ExcludeTerminals && IsTerminalWindow(_foregroundAtKeypress))
                {
                    Trace.Log("跳过：前台是终端窗口，Ctrl+C 视为中断");
                    _armed = false;
                    return;
                }
            }

            double elapsed = ElapsedMs(Stopwatch.GetTimestamp());

            if (seqNow != _seqAtKeypress)
            {
                Trace.Log($"序列号变化 {_seqAtKeypress} -> {seqNow}，耗时 {elapsed:F0}ms");
                _lastSeenSeq = seqNow;
                ResolvePending(finalFailure: false, elapsed);
                return;
            }

            int threshold = _isRemote ? RemoteThresholdMs : LocalThresholdMs;

            if (!_reportedPending && elapsed >= threshold)
            {
                _reportedPending = true;
                Trace.Log($"PENDING 阈值 {threshold}ms 已到，继续等");
                OutcomeReported?.Invoke(new CopyResult
                {
                    Action = _pendingAction,
                    Outcome = CopyOutcome.Pending,
                    ElapsedMs = elapsed,
                    IsRemoteSession = _isRemote,
                    Hint = _isRemote ? "远程桌面剪贴板较慢，继续等待…" : "还没看到剪贴板更新，继续等待…"
                });
            }

            if (elapsed >= ExtendedWindowMs)
            {
                Trace.Log($"TIMEOUT {elapsed:F0}ms，判定失败");
                ResolvePending(finalFailure: true, elapsed);
            }

            return;
        }

        // 没有待处理的快捷键：剪贴板变了说明是别的方式写入的（右键菜单、程序内按钮等）
        if (seqNow != _lastSeenSeq)
        {
            _lastSeenSeq = seqNow;
            if (DetectPassiveCopy) ResolvePassive();
        }
    }

    // ---------- 快捷键路径出结果 ----------
    private void ResolvePending(bool finalFailure, double elapsed)
    {
        KeyAction action = _pendingAction;
        _armed = false;

        if (finalFailure)
        {
            OutcomeReported?.Invoke(new CopyResult
            {
                Action = action,
                Outcome = CopyOutcome.Failed,
                ElapsedMs = elapsed,
                IsRemoteSession = _isRemote,
                Hint = BuildFailureHint(action)
            });
            return;
        }

        var snapshot = ClipboardSnapshot.Capture();
        bool unchanged = snapshot.Signature == _lastSignature && !snapshot.IsEmpty;
        _lastSignature = snapshot.Signature;

        CopyOutcome outcome;
        if (unchanged)
            outcome = CopyOutcome.Unchanged;
        else if (snapshot.Kind == ClipKind.Empty)
            outcome = CopyOutcome.EmptyContent;
        else
            outcome = CopyOutcome.Success;

        Trace.Log($"RESOLVE {action} outcome={outcome} kind={snapshot.Kind} preview=\"{snapshot.Preview}\"");

        OutcomeReported?.Invoke(new CopyResult
        {
            Action = action,
            Outcome = outcome,
            Snapshot = snapshot,
            ElapsedMs = elapsed,
            IsRemoteSession = _isRemote
        });
    }

    // ---------- 无按键路径出结果（右键复制等） ----------
    private void ResolvePassive()
    {
        var snapshot = ClipboardSnapshot.Capture();

        // 空内容静默：有些程序（密码管理器等）会主动清空剪贴板，
        // 那不是用户的操作，弹提示纯属骚扰。
        if (snapshot.Kind == ClipKind.Empty)
        {
            Trace.Log("PASSIVE 空内容，静默");
            _lastSignature = snapshot.Signature;
            return;
        }

        // 内容没变就静默。用户没有主动按键，此时弹「内容未变化」纯属打扰。
        if (snapshot.Signature == _lastSignature)
        {
            Trace.Log("PASSIVE 内容未变化，静默");
            return;
        }

        _lastSignature = snapshot.Signature;

        Trace.Log($"PASSIVE 检测到剪贴板写入 kind={snapshot.Kind} preview=\"{snapshot.Preview}\"");

        OutcomeReported?.Invoke(new CopyResult
        {
            Action = KeyAction.PassiveCopy,
            Outcome = CopyOutcome.Success,
            Snapshot = snapshot,
            IsRemoteSession = _isRemote
        });
    }

    private string BuildFailureHint(KeyAction action)
    {
        string verb = action == KeyAction.Cut ? "剪切" : "复制";

        if (_isRemote)
            return $"远程桌面剪贴板可能已断连，试试在 RDP 里重新勾选剪贴板重定向";

        return $"当前程序可能不允许{verb}，或内容受保护";
    }

    private double ElapsedMs(long now) =>
        (now - _startTimestamp) * 1000.0 / Stopwatch.Frequency;

    // ---------- 终端识别 ----------
    private static readonly HashSet<string> TerminalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "conhost", "WindowsTerminal", "wt",
        "mintty", "bash", "wsl", "OpenConsole", "ubuntu", "debian"
    };

    private static readonly HashSet<string> TerminalClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty"
    };

    /// <summary>
    /// 终端里 Ctrl+C 是 SIGINT 中断信号，不会写剪贴板。
    /// 不排除掉的话，每次中断程序都会误报「复制失败」。
    /// </summary>
    private static bool IsTerminalWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var sb = new StringBuilder(256);
            if (NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 &&
                TerminalClassNames.Contains(sb.ToString()))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            using var proc = Process.GetProcessById((int)pid);
            return TerminalProcessNames.Contains(proc.ProcessName);
        }
        catch
        {
            // 进程可能已退出或权限不足，按「不是终端」处理，宁可多检测也不要漏检测
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _pollTimer.Dispose();
    }
}
