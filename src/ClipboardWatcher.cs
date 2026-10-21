using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CopyCheck;

internal enum CopyOutcome
{
    /// <summary>剪贴板确实被写入了。</summary>
    Success,

    /// <summary>剪贴板被写入了，但内容与上一次完全相同。</summary>
    Unchanged,

    /// <summary>超过首个阈值仍未见更新，但仍在继续等待（远程桌面下常见）。不是最终结论。</summary>
    Pending,

    /// <summary>等到扩展窗口结束仍无更新，判定复制失败。</summary>
    Failed
}

internal sealed class CopyResult
{
    public CopyOutcome Outcome { get; set; }
    public ClipboardSnapshot? Snapshot { get; set; }
    public double ElapsedMs { get; set; }
    public string? Hint { get; set; }
    public bool IsRemoteSession { get; set; }
}

/// <summary>
/// 复制检测引擎。
///
/// 判据不是「用户按了 Ctrl+C」（那只能说明按键到达了系统），而是
/// 「剪贴板序列号是否真的变了」（这才能说明剪贴板被写入过）。
/// 这两者的差别，就是这个工具存在的全部理由。
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    // ---------- 可调参数 ----------
    /// <summary>本机判定「可能没复制上」的等待阈值。</summary>
    public int LocalThresholdMs { get; set; } = 450;

    /// <summary>远程桌面下剪贴板重定向较慢，阈值放宽，避免误报后又要改口。</summary>
    public int RemoteThresholdMs { get; set; } = 1200;

    /// <summary>超过这个时间仍无更新，才最终判定失败。</summary>
    public int ExtendedWindowMs { get; set; } = 2600;

    /// <summary>是否跳过终端类窗口（终端里 Ctrl+C 是中断信号，不是复制）。</summary>
    public bool ExcludeTerminals { get; set; } = true;

    /// <summary>去抖窗口，避免按住键产生的重复触发。</summary>
    private const int DebounceMs = 250;

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
    private uint _seqAtKeypress;
    private IntPtr _foregroundAtKeypress;
    private long _startTimestamp;
    private long _lastTriggerTimestamp;
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

        // 低级键盘钩子：只用来知道「用户想复制」，不做任何判定
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

        Trace.Log($"HOOK 安装成功，remote={_isRemote}, ctx={( _ctx == null ? "null" : "ok")}");
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

            if (info.vkCode == NativeMethods.VK_C && IsPlainCtrl())
            {
                // 只记录事实，处理交给消息循环，避免阻塞键盘输入
                IntPtr fg = NativeMethods.GetForegroundWindow();
                Trace.Log($"捕获 Ctrl+C，前台窗口 0x{fg.ToInt64():X}");
                if (_ctx != null)
                    _ctx.Post(_ => BeginTracking(fg), null);
                else
                    BeginTracking(fg);
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

    // ---------- 开始跟踪一次复制 ----------
    private void BeginTracking(IntPtr foreground)
    {
        long now = Stopwatch.GetTimestamp();

        // 按住不放会连续触发 KEYDOWN，用时间窗口去抖
        if (_armed && ElapsedMs(now) < DebounceMs)
            return;

        _foregroundAtKeypress = foreground;
        _seqAtKeypress = NativeMethods.GetClipboardSequenceNumber();
        _startTimestamp = now;
        _lastTriggerTimestamp = now;
        _armed = true;
        _terminalChecked = false;
        _reportedPending = false;

        Trace.Log($"ARM 序列号基准={_seqAtKeypress}");
    }

    // ---------- 轮询：判断剪贴板到底有没有被写 ----------
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (!_armed) return;

        // 终端判断放在这里做，避免在键盘钩子里查进程阻塞输入
        if (!_terminalChecked)
        {
            _terminalChecked = true;
            if (ExcludeTerminals && IsTerminalWindow(_foregroundAtKeypress))
            {
                Trace.Log("跳过：前台是终端窗口");
                _armed = false;
                return;
            }
        }

        double elapsed = ElapsedMs(Stopwatch.GetTimestamp());

        uint seqNow = NativeMethods.GetClipboardSequenceNumber();
        if (seqNow != _seqAtKeypress)
        {
            Trace.Log($"序列号变化 {_seqAtKeypress} -> {seqNow}，耗时 {elapsed:F0}ms");
            Resolve(finalFailure: false, elapsed);
            return;
        }

        int threshold = _isRemote ? RemoteThresholdMs : LocalThresholdMs;

        if (!_reportedPending && elapsed >= threshold)
        {
            _reportedPending = true;
            Trace.Log($"PENDING 阈值 {threshold}ms 已到，继续等");
            OutcomeReported?.Invoke(new CopyResult
            {
                Outcome = CopyOutcome.Pending,
                ElapsedMs = elapsed,
                IsRemoteSession = _isRemote,
                Hint = _isRemote ? "远程桌面剪贴板较慢，继续等待…" : "还没看到剪贴板更新，继续等待…"
            });
        }

        if (elapsed >= ExtendedWindowMs)
        {
            Trace.Log($"TIMEOUT {elapsed:F0}ms，判定失败");
            Resolve(finalFailure: true, elapsed);
        }
    }

    private void Resolve(bool finalFailure, double elapsed)
    {
        _armed = false;

        if (finalFailure)
        {
            OutcomeReported?.Invoke(new CopyResult
            {
                Outcome = CopyOutcome.Failed,
                ElapsedMs = elapsed,
                IsRemoteSession = _isRemote,
                Hint = BuildFailureHint()
            });
            return;
        }

        var snapshot = ClipboardSnapshot.Capture();
        bool unchanged = snapshot.Signature == _lastSignature && !snapshot.IsEmpty;
        _lastSignature = snapshot.Signature;

        Trace.Log($"RESOLVE outcome={(unchanged ? "Unchanged" : "Success")} kind={snapshot.Kind} preview=\"{snapshot.Preview}\"");

        OutcomeReported?.Invoke(new CopyResult
        {
            Outcome = unchanged ? CopyOutcome.Unchanged : CopyOutcome.Success,
            Snapshot = snapshot,
            ElapsedMs = elapsed,
            IsRemoteSession = _isRemote
        });
    }

    private string BuildFailureHint()
    {
        if (_isRemote)
            return "远程桌面剪贴板可能已断连，试试在 RDP 里重新勾选剪贴板重定向";

        return "当前程序可能不允许复制，或内容受保护";
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
