using System;
using System.Runtime.InteropServices;

namespace CopyCheck;

/// <summary>
/// 所有 Win32 API 声明集中在这里，方便审计和移植。
/// </summary>
internal static class NativeMethods
{
    // ---------- 键盘钩子 ----------
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_SYSKEYDOWN = 0x0104;

    internal const int VK_CONTROL = 0x11;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_MENU = 0x12;   // Alt
    internal const int VK_C = 0x43;
    internal const int VK_X = 0x58;
    internal const int VK_V = 0x56;

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    // ---------- 剪贴板 ----------
    /// <summary>
    /// 剪贴板每被写入一次，这个序列号就 +1。
    /// 序列号没变 == 剪贴板根本没被写过 == 复制真的失败了。这是本工具的核心判据。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    // ---------- 窗口 / 会话 ----------
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    internal const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    // ---------- 屏幕 ----------
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>是否运行在远程桌面会话中。RDP 的剪贴板重定向延迟远高于本机。</summary>
    internal static bool IsRemoteSession() => GetSystemMetrics(SM_REMOTESESSION) != 0;
}
