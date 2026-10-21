using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CopyCheck;

/// <summary>
/// 用代码画托盘图标，不依赖外部 ico 文件。
/// 深色圆角底 + 绿色对勾，缩到 16px 也认得出。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create()
    {
        const int size = 32;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 圆角底
            using (var path = Rounded(new Rectangle(2, 2, size - 4, size - 4), 8))
            using (var bg = new LinearGradientBrush(
                new Rectangle(2, 2, size - 4, size - 4),
                Color.FromArgb(39, 39, 42),
                Color.FromArgb(24, 24, 27),
                45f))
            {
                g.FillPath(bg, path);
            }

            // 对勾
            using var pen = new Pen(Color.FromArgb(52, 211, 153), 3.4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            g.DrawLines(pen, new[]
            {
                new PointF(9.5f, 16.5f),
                new PointF(14f, 21f),
                new PointF(22.5f, 11.5f)
            });
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            // Clone 之后才能安全释放原 handle，否则会泄漏 GDI 对象
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
