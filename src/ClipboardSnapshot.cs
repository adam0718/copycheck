using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CopyCheck;

internal enum ClipKind
{
    Empty,
    Text,
    Image,
    Files,
    Unknown
}

/// <summary>
/// 一次剪贴板内容的快照：类型、可读预览、以及用于判断「内容是否与上次相同」的签名。
/// </summary>
internal sealed class ClipboardSnapshot
{
    public ClipKind Kind { get; private set; } = ClipKind.Empty;

    /// <summary>给人看的一行预览，已压缩空白并截断。</summary>
    public string Preview { get; private set; } = "";

    /// <summary>右侧的量化标签，如「28 字」「1920×1080」「3 个文件」。</summary>
    public string CountLabel { get; private set; } = "";

    /// <summary>内容签名，用于判断连续两次复制的内容是否一致。</summary>
    public string Signature { get; private set; } = "";

    public bool IsEmpty => Kind == ClipKind.Empty || Kind == ClipKind.Unknown;

    private const int PreviewMaxChars = 34;

    /// <summary>
    /// 读取当前剪贴板。剪贴板可能被其他程序短暂占用，因此带重试。
    /// 必须在 STA 线程调用（WinForms 的 Clipboard 类要求）。
    /// </summary>
    public static ClipboardSnapshot Capture()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return CaptureOnce();
            }
            catch (ExternalException)
            {
                // 剪贴板被别的进程锁住，稍等再试
                Thread.Sleep(25);
            }
            catch (Exception)
            {
                break;
            }
        }

        return new ClipboardSnapshot
        {
            Kind = ClipKind.Unknown,
            Preview = "（剪贴板暂时读不出来）",
            CountLabel = "读取失败",
            Signature = "unknown"
        };
    }

    private static ClipboardSnapshot CaptureOnce()
    {
        // 文件优先：只有真的复制了文件才有 FileDropList，判定最明确
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0)
            {
                var names = new List<string>();
                foreach (string? f in files)
                {
                    if (!string.IsNullOrEmpty(f))
                        names.Add(Path.GetFileName(f));
                }

                string first = names.Count > 0 ? names[0] : "文件";
                return new ClipboardSnapshot
                {
                    Kind = ClipKind.Files,
                    Preview = files.Count == 1 ? first : $"{first} 等 {files.Count} 项",
                    CountLabel = $"{files.Count} 个文件",
                    Signature = "F|" + string.Join("|", files.Cast<string>())
                };
            }
        }

        // 文本其次：文本预览对「复制对了吗」这个问题信息量最大
        if (Clipboard.ContainsText())
        {
            string text = Clipboard.GetText() ?? "";
            if (text.Length > 0)
            {
                string flat = Flatten(text);
                return new ClipboardSnapshot
                {
                    Kind = ClipKind.Text,
                    Preview = Truncate(flat, PreviewMaxChars),
                    CountLabel = $"{text.Length} 字",
                    Signature = $"T|{text.Length}|{Head(text, 120)}|{Tail(text, 120)}"
                };
            }

            return new ClipboardSnapshot
            {
                // 有文本格式但内容为空，对用户来说和「剪贴板是空的」没有区别：
                // 都是没有东西可粘贴。归为 Empty，才能触发「内容是空的」提示。
                Kind = ClipKind.Empty,
                Preview = "剪贴板是空的",
                CountLabel = "空",
                Signature = "T|empty"
            };
        }

        // 图片第三
        if (Clipboard.ContainsImage())
        {
            var img = Clipboard.GetImage();
            if (img != null)
            {
                return new ClipboardSnapshot
                {
                    Kind = ClipKind.Image,
                    Preview = $"图片 {img.Width}×{img.Height}",
                    CountLabel = "图片",
                    Signature = $"I|{img.Width}x{img.Height}|{img.PixelFormat}"
                };
            }
        }

        if (Clipboard.ContainsAudio())
        {
            return new ClipboardSnapshot
            {
                Kind = ClipKind.Unknown,
                Preview = "音频数据",
                CountLabel = "音频",
                Signature = "A"
            };
        }

        return new ClipboardSnapshot
        {
            Kind = ClipKind.Empty,
            Preview = "剪贴板为空",
            CountLabel = "空",
            Signature = "empty"
        };
    }

    /// <summary>把换行、制表符、连续空格压成单个空格，便于单行显示。</summary>
    private static string Flatten(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, 512));
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            bool isSpace = c is ' ' or '\t' or '\r' or '\n' or '\u3000';
            if (isSpace)
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;

            if (sb.Length > 400) break;
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "…";
    }

    /// <summary>给「试一下提示效果」用的假快照，不读真实剪贴板。</summary>
    public static ClipboardSnapshot CreateDemo(string preview, string countLabel) => new ClipboardSnapshot
    {
        Kind = ClipKind.Text,
        Preview = preview,
        CountLabel = countLabel,
        Signature = "demo"
    };

    private static string Head(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

    private static string Tail(string s, int n) => s.Length <= n ? s : s.Substring(s.Length - n);
}
