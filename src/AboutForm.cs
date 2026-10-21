using System;
using System.Drawing;
using System.Windows.Forms;

namespace CopyCheck;

internal sealed class AboutForm : Form
{
    public AboutForm(bool isRemoteSession)
    {
        Text = "关于 CopyCheck";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(460, 392);
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.FromArgb(228, 228, 231);
        Font = new Font("Microsoft YaHei UI", 9f);

        int y = 20;

        AddLabel("CopyCheck · 复制核对", 20, ref y, 13f, FontStyle.Bold, Color.FromArgb(245, 245, 247));
        AddLabel("v0.1.0  ·  MIT License", 20, ref y, 8.5f, FontStyle.Regular, Color.FromArgb(113, 113, 122));

        y += 6;
        AddLabel("复制之后，告诉你到底复制上了没有。", 20, ref y, 9.5f, FontStyle.Regular, Color.FromArgb(161, 161, 170));

        y += 12;
        AddLabel("它怎么判断的", 20, ref y, 9.5f, FontStyle.Bold, Color.FromArgb(212, 212, 216));
        AddLabel(
            "不看你按没按 Ctrl+C —— 那只能证明按键到了系统。\n" +
            "它看的是 Windows 的剪贴板序列号：剪贴板每被写入一次，\n" +
            "这个数字就加一。数字没变，就是真的没复制上。",
            20, ref y, 9f, FontStyle.Regular, Color.FromArgb(161, 161, 170));

        y += 10;
        AddLabel("三种提示", 20, ref y, 9.5f, FontStyle.Bold, Color.FromArgb(212, 212, 216));
        AddLabel("✓  已复制         剪贴板确实更新了，下面显示复制到的内容", 20, ref y, 9f, FontStyle.Regular, Color.FromArgb(52, 211, 153));
        AddLabel("=  内容未变化     写进去了，但和你上次复制的一模一样", 20, ref y, 9f, FontStyle.Regular, Color.FromArgb(251, 191, 36));
        AddLabel("✗  未复制成功     等到最后也没看到剪贴板更新", 20, ref y, 9f, FontStyle.Regular, Color.FromArgb(248, 113, 113));

        y += 10;
        string session = isRemoteSession
            ? "远程桌面会话 —— 剪贴板走重定向，判定阈值已自动放宽到 1200ms"
            : "本机会话 —— 判定阈值 450ms";
        AddLabel("当前环境", 20, ref y, 9.5f, FontStyle.Bold, Color.FromArgb(212, 212, 216));
        AddLabel(session, 20, ref y, 9f, FontStyle.Regular, Color.FromArgb(161, 161, 170));

        var close = new Button
        {
            Text = "知道了",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.FromArgb(228, 228, 231),
            Size = new Size(96, 32),
            Location = new Point(ClientSize.Width - 116, ClientSize.Height - 48)
        };
        close.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
        Controls.Add(close);
        AcceptButton = close;
    }

    private void AddLabel(string text, int x, ref int y, float size, FontStyle style, Color color)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            Font = new Font("Microsoft YaHei UI", size, style),
            ForeColor = color,
            BackColor = Color.Transparent
        };
        Controls.Add(label);
        y += label.PreferredHeight + 3;
    }
}
