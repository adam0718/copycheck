using System;
using System.Diagnostics;
using System.IO;

namespace CopyCheck;

/// <summary>
/// 轻量诊断日志。默认关闭，设置环境变量 COPYCHECK_TRACE=1 才写文件。
/// 只在排查「为什么没弹提示」时开启，平时不产生任何磁盘写入。
/// </summary>
internal static class Trace
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("COPYCHECK_TRACE") == "1";

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CopyCheck", "trace.log");

    public static void Log(string message)
    {
        if (!Enabled) return;

        try
        {
            string dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);

            lock (Gate)
            {
                File.AppendAllText(LogPath, $"[{Clock.ElapsedMilliseconds,7}ms] {message}\n");
            }
        }
        catch
        {
            // 诊断日志失败绝不能影响主流程
        }
    }

    public static void Reset()
    {
        if (!Enabled) return;
        try
        {
            if (File.Exists(LogPath)) File.Delete(LogPath);
        }
        catch { }
    }
}
