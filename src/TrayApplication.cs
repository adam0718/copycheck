using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CopyCheck;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly ToastForm _toast;
    private readonly ClipboardWatcher _watcher;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;

    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _terminalItem;
    private readonly ToolStripMenuItem _pasteItem;
    private readonly ToolStripMenuItem _passiveItem;
    private readonly System.Windows.Forms.Timer? _demoTimer;

    private bool _initializing = true;

    public TrayApplication(string? demoMode = null)
    {
        Trace.Log("Tray: 开始构造");
        _settings = AppSettings.Load();
        Trace.Log("Tray: 配置已载入");

        // 先创建提示窗并强制生成句柄，这样 WinForms 会装好同步上下文，
        // 后面的键盘钩子才能安全地把事件投递回 UI 线程。
        _toast = new ToastForm();
        Trace.Log("Tray: ToastForm 已构造");
        _ = _toast.Handle;
        Trace.Log("Tray: ToastForm 句柄已创建");

        _watcher = new ClipboardWatcher
        {
            ExcludeTerminals = _settings.ExcludeTerminals,
            DetectPassiveCopy = _settings.DetectPassiveCopy,
            ShowPasteToast = _settings.ShowPasteToast,
            LocalThresholdMs = _settings.LocalThresholdMs,
            RemoteThresholdMs = _settings.RemoteThresholdMs,
            ExtendedWindowMs = _settings.ExtendedWindowMs
        };
        _watcher.OutcomeReported += OnOutcomeReported;
        Trace.Log("Tray: Watcher 已构造");

        _icon = TrayIconFactory.Create();
        Trace.Log("Tray: 托盘图标已生成");

        _autoStartItem = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled
        };
        _autoStartItem.CheckedChanged += OnAutoStartChanged;

        _terminalItem = new ToolStripMenuItem("跳过终端窗口")
        {
            CheckOnClick = true,
            Checked = _settings.ExcludeTerminals,
            ToolTipText = "终端里 Ctrl+C 是中断信号不是复制，勾选可避免误报"
        };
        _terminalItem.CheckedChanged += OnTerminalSettingChanged;

        _pasteItem = new ToolStripMenuItem("粘贴时提示")
        {
            CheckOnClick = true,
            Checked = _settings.ShowPasteToast,
            ToolTipText = "Ctrl+V 时显示粘的是什么内容"
        };
        _pasteItem.CheckedChanged += OnPasteSettingChanged;

        _passiveItem = new ToolStripMenuItem("检测右键复制")
        {
            CheckOnClick = true,
            Checked = _settings.DetectPassiveCopy,
            ToolTipText = "右键菜单的复制、程序内复制按钮等没有快捷键的复制"
        };
        _passiveItem.CheckedChanged += OnPassiveSettingChanged;

        var menu = new ContextMenuStrip();

        var demoMenu = new ToolStripMenuItem("试一下提示效果")
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        demoMenu.DropDownItems.Add("已复制", null, (_, _) => ShowDemoMode("copy"));
        demoMenu.DropDownItems.Add("已剪切", null, (_, _) => ShowDemoMode("cut"));
        demoMenu.DropDownItems.Add("已粘贴", null, (_, _) => ShowDemoMode("paste"));
        demoMenu.DropDownItems.Add("内容未变化", null, (_, _) => ShowDemoMode("unchanged"));
        demoMenu.DropDownItems.Add("内容是空的", null, (_, _) => ShowDemoMode("empty"));
        demoMenu.DropDownItems.Add("未复制成功", null, (_, _) => ShowDemoMode("failed"));

        menu.Items.Add(demoMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pasteItem);
        menu.Items.Add(_passiveItem);
        menu.Items.Add(_terminalItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("关于", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Shutdown()));

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "CopyCheck · 复制核对",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowStatusBalloon();
        Trace.Log("Tray: 托盘已挂载");

        _initializing = false;

        // 演示模式只渲染提示，不装钩子、不监听剪贴板
        if (demoMode == null)
        {
            try
            {
                _watcher.Start();
                Trace.Log("Tray 就绪，开始监听");
            }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(5000, "CopyCheck 启动失败",
                    ex.Message, ToolTipIcon.Error);
            }
        }

        // 演示模式：把显示动作排到消息循环里执行。
        // 不能用 Timer —— 构造函数执行时 Application.Run 还没开始，没有消息循环，Tick 不会来。
        if (demoMode != null)
        {
            var ctx = System.Threading.SynchronizationContext.Current;
            if (ctx != null)
            {
                ctx.Post(state => ShowDemoMode((string)state!), demoMode);
            }
            else
            {
                _demoTimer = new System.Windows.Forms.Timer { Interval = 600 };
                _demoTimer.Tick += (_, _) =>
                {
                    _demoTimer.Stop();
                    ShowDemoMode(demoMode);
                };
                _demoTimer.Start();
            }
        }
    }

    // ---------- 检测结果 → 提示窗 ----------
    private void OnOutcomeReported(CopyResult result)
    {
        _toast.Present(result);
    }

    // ---------- 菜单回调 ----------
    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        if (_initializing) return;

        if (!AutoStart.Set(_autoStartItem.Checked))
        {
            _autoStartItem.Checked = AutoStart.IsEnabled;
            _tray.ShowBalloonTip(3000, "CopyCheck",
                "修改开机自启失败，可能被安全软件拦截了", ToolTipIcon.Warning);
        }
    }

    private void OnTerminalSettingChanged(object? sender, EventArgs e)
    {
        if (_initializing) return;

        _settings.ExcludeTerminals = _terminalItem.Checked;
        _watcher.ExcludeTerminals = _terminalItem.Checked;
        _settings.Save();
    }

    private void OnPasteSettingChanged(object? sender, EventArgs e)
    {
        if (_initializing) return;

        _settings.ShowPasteToast = _pasteItem.Checked;
        _watcher.ShowPasteToast = _pasteItem.Checked;
        _settings.Save();
    }

    private void OnPassiveSettingChanged(object? sender, EventArgs e)
    {
        if (_initializing) return;

        _settings.DetectPassiveCopy = _passiveItem.Checked;
        _watcher.DetectPassiveCopy = _passiveItem.Checked;
        _settings.Save();
    }

    /// <summary>按模式直接渲染一条假结果，用于预览外观和自动化测试。</summary>
    private void ShowDemoMode(string mode)
    {
        Trace.Log($"演示模式渲染: {mode}");

        var sample = ClipboardSnapshot.CreateDemo(
            "这是一条用来预览提示效果的测试文本，中英文 mixed 123",
            "28 字");
        var emptySnap = ClipboardSnapshot.CreateDemo("剪贴板为空", "空");

        CopyResult result = mode switch
        {
            "cut" => new CopyResult
            {
                Action = KeyAction.Cut,
                Outcome = CopyOutcome.Success,
                Snapshot = sample
            },
            "paste" => new CopyResult
            {
                Action = KeyAction.Paste,
                Outcome = CopyOutcome.Success,
                Snapshot = sample
            },
            "unchanged" => new CopyResult
            {
                Action = KeyAction.Copy,
                Outcome = CopyOutcome.Unchanged,
                Snapshot = sample
            },
            "empty" => new CopyResult
            {
                Action = KeyAction.Copy,
                Outcome = CopyOutcome.EmptyContent,
                Snapshot = emptySnap,
                Hint = "没有选中任何内容，或当前程序没往剪贴板写数据"
            },
            "failed" => new CopyResult
            {
                Action = KeyAction.Copy,
                Outcome = CopyOutcome.Failed,
                Hint = "当前程序可能不允许复制，或内容受保护"
            },
            _ => new CopyResult
            {
                Action = KeyAction.Copy,
                Outcome = CopyOutcome.Success,
                Snapshot = sample
            }
        };

        result.ElapsedMs = 12;
        result.IsRemoteSession = _watcher.IsRemoteSession;
        _toast.Present(result);
    }

    private void ShowStatusBalloon()
    {
        string mode = _watcher.IsRemoteSession ? "远程桌面会话（阈值已放宽）" : "本机";
        _tray.ShowBalloonTip(2500, "CopyCheck 正在运行",
            $"复制、剪切、粘贴都会告诉你结果。\n当前：{mode}",
            ToolTipIcon.Info);
    }

    private void ShowAbout()
    {
        using var form = new AboutForm(_watcher.IsRemoteSession);
        form.ShowDialog();
    }

    private void Shutdown()
    {
        _watcher.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _toast.Dispose();
        _icon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
