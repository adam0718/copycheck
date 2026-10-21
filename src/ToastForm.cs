using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CopyCheck;

/// <summary>
/// 屏幕底部的提示窗。
/// 三个硬要求：不抢焦点、不吃鼠标、不进 Alt+Tab —— 否则它就从「提示」变成了「打扰」。
/// </summary>
internal sealed class ToastForm : Form
{
    private const int Radius = 10;
    private const int PadH = 18;
    private const int PadV = 14;
    private const int IconSize = 26;
    private const int Gap = 13;
    private const int MinWidth = 300;
    private const int MaxWidth = 640;
    private const int FadeInMs = 90;
    private const int FadeOutMs = 200;

    private enum Phase { Hidden, FadeIn, Hold, FadeOut }

    private readonly System.Windows.Forms.Timer _animTimer;
    private readonly Stopwatch _phaseClock = new();

    private Phase _phase = Phase.Hidden;
    private int _holdMs = 1500;

    // 当前展示的内容
    private CopyOutcome _outcome = CopyOutcome.Success;
    private string _title = "";
    private string _preview = "";
    private string _label = "";

    private readonly Font _titleFont;
    private readonly Font _previewFont;
    private readonly Font _labelFont;

    private float UiScale => DeviceDpi / 96f;

    public ToastForm()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw, true);

        // 自绘窗口必须关掉自动缩放，否则 Show() 时 WinForms 会按字体比例
        // 二次缩放 Size，导致窗口尺寸和我们算的对不上、Region 也错位。
        AutoScaleMode = AutoScaleMode.None;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(24, 24, 27);
        DoubleBuffered = true;
        Opacity = 0;
        Visible = false;

        _titleFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        _previewFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _labelFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += OnAnimTick;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW  不进 Alt+Tab
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE  点击不激活
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT 鼠标穿透
            return cp;
        }
    }

    // ---------- 对外接口 ----------
    public void Present(CopyResult result)
    {
        Trace.Log($"TOAST.Present outcome={result.Outcome} Visible={Visible} phase={_phase}");
        ApplyContent(result);

        if (!Visible || _phase == Phase.Hidden || _phase == Phase.FadeOut)
        {
            RecalcLayout();
            Reposition();
            Invalidate();

            _phase = Phase.FadeIn;
            _phaseClock.Restart();
            _animTimer.Start();

            if (!Visible) Show();

            Trace.Log($"TOAST 已显示 size={Width}x{Height} bounds={Bounds} " +
                      $"hasRegion={Region != null} dpi={DeviceDpi} " +
                      $"screen={Screen.FromPoint(Cursor.Position).WorkingArea}");
        }
        else
        {
            // 已经在显示（例如 Pending → Success 的原地改口），只换内容不闪烁
            Reposition();
            Invalidate();
            _phase = Phase.Hold;
            _phaseClock.Restart();
        }
    }

    private void ApplyContent(CopyResult r)
    {
        _outcome = r.Outcome;

        switch (r.Outcome)
        {
            case CopyOutcome.Success:
                _title = "已复制";
                _preview = r.Snapshot?.Preview ?? "";
                _label = r.Snapshot?.CountLabel ?? "";
                _holdMs = 1500;
                break;

            case CopyOutcome.Unchanged:
                _title = "内容未变化";
                _preview = r.Snapshot?.Preview ?? "";
                _label = r.Snapshot?.CountLabel ?? "";
                _holdMs = 1800;
                break;

            case CopyOutcome.Pending:
                _title = "还没检测到剪贴板更新";
                _preview = r.Hint ?? "";
                _label = "";
                _holdMs = 4000;   // 兜底，正常情况下很快会被最终结果覆盖
                break;

            case CopyOutcome.Failed:
                _title = "未复制成功";
                _preview = r.Hint ?? "";
                _label = "";
                _holdMs = 2800;
                break;
        }
    }

    private Color AccentColor => _outcome switch
    {
        CopyOutcome.Success => Color.FromArgb(52, 211, 153),
        CopyOutcome.Unchanged => Color.FromArgb(251, 191, 36),
        CopyOutcome.Pending => Color.FromArgb(148, 163, 184),
        _ => Color.FromArgb(248, 113, 113)
    };

    // ---------- 尺寸与定位 ----------
    private void RecalcLayout()
    {
        float s = UiScale;

        var titleSize = TextRenderer.MeasureText(_title, _titleFont);
        var previewSize = string.IsNullOrEmpty(_preview)
            ? Size.Empty
            : TextRenderer.MeasureText(_preview, _previewFont);
        var labelSize = string.IsNullOrEmpty(_label)
            ? Size.Empty
            : TextRenderer.MeasureText(_label, _labelFont);

        int textWidth = Math.Max(titleSize.Width, previewSize.Width);
        int width = (int)((PadH * 2 + IconSize + Gap) * s) + textWidth + (int)(12 * s) + labelSize.Width;

        width = Num.Clamp(width, (int)(MinWidth * s), (int)(MaxWidth * s));

        int textHeight = titleSize.Height + (previewSize.Height > 0 ? (int)(3 * s) + previewSize.Height : 0);
        int height = Math.Max((int)(IconSize * s), textHeight) + (int)(PadV * 2 * s);

        Size = new Size(width, height);
        ApplyRegion();
    }

    /// <summary>让窗口形状跟当前尺寸一致。只在布局计算后调用，
    /// 绝不能挂在 OnResize 上 —— 设置 Region 会再触发一次 Resize，形成死循环。</summary>
    private void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;

        using var path = BuildRoundedPath(
            new Rectangle(0, 0, Width, Height), (int)(Radius * UiScale));
        Region = new Region(path);
    }

    private void Reposition()
    {
        // 出现在鼠标所在那块屏幕，而不是永远主屏
        var screen = Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;

        int x = wa.Left + (wa.Width - Width) / 2;
        int y = wa.Bottom - Height - (int)(64 * UiScale);
        Location = new Point(x, y);
    }

    private static GraphicsPath BuildRoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;

        if (d <= 0 || d > Math.Min(rect.Width, rect.Height))
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------- 绘制 ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float s = UiScale;
        var accent = AccentColor;

        // 底色
        using (var bg = new SolidBrush(Color.FromArgb(238, 24, 24, 27)))
        using (var path = BuildRoundedPath(new Rectangle(0, 0, Width, Height), (int)(Radius * s)))
        {
            g.FillPath(bg, path);
        }

        // 左侧状态色竖条，比图标更快被眼睛捕捉
        using (var bar = new SolidBrush(accent))
        {
            g.FillRectangle(bar, 0, (int)(PadV * s * 0.7), (int)(3 * s), Height - (int)(PadV * s * 1.4));
        }

        // 图标圆
        int iconX = (int)(PadH * s);
        int iconY = (Height - (int)(IconSize * s)) / 2;
        int iconW = (int)(IconSize * s);

        using (var iconBg = new SolidBrush(Color.FromArgb(46, accent)))
        {
            g.FillEllipse(iconBg, iconX, iconY, iconW, iconW);
        }

        DrawIconGlyph(g, new Rectangle(iconX, iconY, iconW, iconW), accent, (int)(2f * s));

        // 文字块
        int textX = iconX + iconW + (int)(Gap * s);
        int titleH = TextRenderer.MeasureText(_title, _titleFont).Height;
        int previewH = string.IsNullOrEmpty(_preview)
            ? 0
            : TextRenderer.MeasureText(_preview, _previewFont).Height;

        int blockH = titleH + (previewH > 0 ? (int)(3 * s) + previewH : 0);
        int textY = (Height - blockH) / 2;

        TextRenderer.DrawText(g, _title, _titleFont,
            new Point(textX, textY), Color.FromArgb(245, 245, 247),
            TextFormatFlags.NoPadding);

        if (previewH > 0)
        {
            TextRenderer.DrawText(g, _preview, _previewFont,
                new Point(textX, textY + titleH + (int)(3 * s)), Color.FromArgb(161, 161, 170),
                TextFormatFlags.NoPadding);
        }

        // 右侧量化标签
        if (!string.IsNullOrEmpty(_label))
        {
            var labelSize = TextRenderer.MeasureText(_label, _labelFont);
            TextRenderer.DrawText(g, _label, _labelFont,
                new Point(Width - (int)(PadH * s) - labelSize.Width,
                          (Height - labelSize.Height) / 2),
                Color.FromArgb(113, 113, 122),
                TextFormatFlags.NoPadding);
        }

        // 顶部一条极细的进度线，暗示「还会显示多久」
        if (_phase == Phase.Hold && _holdMs > 0)
        {
            double remain = Num.Clamp(1 - _phaseClock.Elapsed.TotalMilliseconds / _holdMs, 0, 1);
            using var line = new SolidBrush(Color.FromArgb(90, accent));
            g.FillRectangle(line, 0, 0, (int)(Width * remain), Math.Max(1, (int)(1.5f * s)));
        }
    }

    /// <summary>用几何图形画状态符号，不依赖任何字体，避免缺字方框。</summary>
    private void DrawIconGlyph(Graphics g, Rectangle box, Color color, int thickness)
    {
        float cx = box.X + box.Width / 2f;
        float cy = box.Y + box.Height / 2f;
        float u = box.Width * 0.22f;   // 单位长度

        using var pen = new Pen(color, Math.Max(1.6f, thickness))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (_outcome)
        {
            case CopyOutcome.Success:
                // 对勾
                g.DrawLines(pen, new[]
                {
                    new PointF(cx - u, cy),
                    new PointF(cx - u * 0.25f, cy + u * 0.7f),
                    new PointF(cx + u, cy - u * 0.65f)
                });
                break;

            case CopyOutcome.Failed:
                // 叉
                g.DrawLine(pen, cx - u * 0.8f, cy - u * 0.8f, cx + u * 0.8f, cy + u * 0.8f);
                g.DrawLine(pen, cx + u * 0.8f, cy - u * 0.8f, cx - u * 0.8f, cy + u * 0.8f);
                break;

            case CopyOutcome.Unchanged:
                // 等号
                g.DrawLine(pen, cx - u * 0.8f, cy - u * 0.35f, cx + u * 0.8f, cy - u * 0.35f);
                g.DrawLine(pen, cx - u * 0.8f, cy + u * 0.35f, cx + u * 0.8f, cy + u * 0.35f);
                break;

            default:
                // 三个点，表示「还在等」
                using (var dot = new SolidBrush(color))
                {
                    float r = Math.Max(1.4f, u * 0.26f);
                    g.FillEllipse(dot, cx - u * 0.75f - r, cy - r, r * 2, r * 2);
                    g.FillEllipse(dot, cx - r, cy - r, r * 2, r * 2);
                    g.FillEllipse(dot, cx + u * 0.75f - r, cy - r, r * 2, r * 2);
                }
                break;
        }
    }

    // ---------- 动画 ----------
    private void OnAnimTick(object? sender, EventArgs e)
    {
        double elapsed = _phaseClock.Elapsed.TotalMilliseconds;

        switch (_phase)
        {
            case Phase.FadeIn:
                Opacity = Num.Clamp(elapsed / FadeInMs, 0, 1);
                if (elapsed >= FadeInMs)
                {
                    Opacity = 1;
                    _phase = Phase.Hold;
                    _phaseClock.Restart();
                    Trace.Log("TOAST 淡入完成，进入停留");
                }
                break;

            case Phase.Hold:
                if (elapsed >= _holdMs)
                {
                    _phase = Phase.FadeOut;
                    _phaseClock.Restart();
                }
                Invalidate(); // 让顶部进度线动起来
                break;

            case Phase.FadeOut:
                Opacity = Num.Clamp(1 - elapsed / FadeOutMs, 0, 1);
                if (elapsed >= FadeOutMs)
                {
                    _animTimer.Stop();
                    _phase = Phase.Hidden;
                    Opacity = 0;
                    Hide();
                }
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Dispose();
            _titleFont.Dispose();
            _previewFont.Dispose();
            _labelFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
