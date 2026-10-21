using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CopyCheck;

internal sealed class AppSettings
{
    /// <summary>终端里 Ctrl+C 是中断信号，默认排除，避免天天误报。</summary>
    public bool ExcludeTerminals { get; set; } = true;

    public int LocalThresholdMs { get; set; } = 450;
    public int RemoteThresholdMs { get; set; } = 1200;
    public int ExtendedWindowMs { get; set; } = 2600;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CopyCheck");

    private static string FilePath => Path.Combine(Dir, "settings.ini");

    /// <summary>
    /// 用最简单的 key=value 格式，不引入 JSON 序列化库。
    /// 配置项一共就四个，可读性比结构化更重要。
    /// </summary>
    public static AppSettings Load()
    {
        var settings = new AppSettings();

        try
        {
            if (!File.Exists(FilePath)) return settings;

            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "ExcludeTerminals":
                        settings.ExcludeTerminals = val == "1" ||
                            val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;

                    case "LocalThresholdMs":
                        if (int.TryParse(val, out int local) && local > 0)
                            settings.LocalThresholdMs = local;
                        break;

                    case "RemoteThresholdMs":
                        if (int.TryParse(val, out int remote) && remote > 0)
                            settings.RemoteThresholdMs = remote;
                        break;

                    case "ExtendedWindowMs":
                        if (int.TryParse(val, out int ext) && ext > 0)
                            settings.ExtendedWindowMs = ext;
                        break;
                }
            }
        }
        catch
        {
            // 配置坏了不该让整个程序起不来，退回默认值
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[]
            {
                "# CopyCheck 配置",
                "# ExcludeTerminals: 1=跳过终端窗口（终端里 Ctrl+C 是中断信号）",
                $"ExcludeTerminals={(ExcludeTerminals ? 1 : 0)}",
                $"LocalThresholdMs={LocalThresholdMs}",
                $"RemoteThresholdMs={RemoteThresholdMs}",
                $"ExtendedWindowMs={ExtendedWindowMs}"
            });
        }
        catch
        {
            // 存不下就算了，不影响主功能
        }
    }
}

internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CopyCheck";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return false;

                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"",
                        RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
