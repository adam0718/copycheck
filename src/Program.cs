using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CopyCheck;

internal static class Program
{
    private const string MutexName = @"Local\CopyCheck.SingleInstance.v1";

    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CopyCheck");

    [STAThread]
    private static void Main()
    {
        // 先解析演示参数：演示模式不参与单实例检查，也不装键盘钩子。
        // 它只是一次性的「渲染一张提示给你看」，和正常实例互不干扰。
        string? demoMode = ParseDemoArg();

        Mutex? mutex = null;
        if (demoMode == null)
        {
            mutex = new Mutex(true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                MessageBox.Show(
                    "CopyCheck 已经在运行了，看一下右下角托盘。",
                    "CopyCheck",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                mutex.Dispose();
                return;
            }
        }

        try
        {
            // 高 DPI 感知在 app.manifest 里声明（net48 没有 Application.SetHighDpiMode）
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Trace.Reset();
            Trace.Log("CopyCheck 启动");
            if (demoMode != null) Trace.Log($"演示模式: {demoMode}");

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => HandleFatal(e.Exception, "UI 线程异常");
            AppDomain.CurrentDomain.UnhandledException +=
                (s, e) => HandleFatal(e.ExceptionObject as Exception, "未处理异常");

            Trace.Log("进入消息循环");
            Application.Run(new TrayApplication(demoMode));
            Trace.Log("消息循环已返回 —— 程序即将退出");
        }
        catch (Exception ex)
        {
            HandleFatal(ex, "启动失败");
        }
        finally
        {
            if (mutex != null)
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
            }
        }
    }

    /// <summary>解析 --demo=copy|cut|paste|unchanged|empty|failed</summary>
    private static string? ParseDemoArg()
    {
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase))
                return arg.Substring("--demo=".Length).Trim().ToLowerInvariant();
        }
        return null;
    }

    private static void HandleFatal(Exception? ex, string stage)
    {
        if (ex == null) return;

        try
        {
            Directory.CreateDirectory(LogDir);
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + stage + ": " + ex + "\r\n\r\n";
            File.AppendAllText(Path.Combine(LogDir, "error.log"), line);
        }
        catch
        {
            // 日志写不进去也不能让程序崩在这里
        }

        MessageBox.Show(
            "CopyCheck 遇到问题：\r\n\r\n" + ex.Message + "\r\n\r\n详细信息已写入：\r\n" +
            Path.Combine(LogDir, "error.log"),
            "CopyCheck 出错了",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
