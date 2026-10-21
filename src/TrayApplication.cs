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

    private bool _initializing = true;

    public TrayApplication()
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

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("试一下提示效果", null, (_, _) => ShowDemo()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
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

    private void ShowDemo()
    {
        var snapshot = ClipboardSnapshot.Capture();
        if (snapshot.IsEmpty)
        {
            snapshot = ClipboardSnapshot.CreateDemo(
                "这是一条测试文本，用来预览提示效果",
                "18 字");
        }

        _toast.Present(new CopyResult
        {
            Outcome = CopyOutcome.Success,
            Snapshot = snapshot,
            ElapsedMs = 12,
            IsRemoteSession = _watcher.IsRemoteSession
        });
    }

    private void ShowStatusBalloon()
    {
        string mode = _watcher.IsRemoteSession ? "远程桌面会话（阈值已放宽）" : "本机";
        _tray.ShowBalloonTip(2500, "CopyCheck 正在运行",
            $"复制任意内容试试，我会告诉你到底复制上了没有。\n当前：{mode}",
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
