"""
CopyCheck 端到端验证。

做四件事：
1. 启动程序，确认进程活着（没在启动阶段崩溃）
2. 模拟「按了 Ctrl+C 但剪贴板没变」→ 应该报失败
3. 模拟「按了 Ctrl+C 且剪贴板确实写入」→ 应该报成功并显示预览
4. 每一步截图，用眼睛确认提示窗真的画出来了

全程只用 Python + Win32 API，不依赖 PowerShell。
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

EXE = os.environ.get("COPYCHECK_EXE") or os.path.join(
    os.path.dirname(__file__), "..", "dist", "CopyCheck.exe")
SHOT_DIR = os.path.join(os.path.dirname(__file__), "shots")

VK_CONTROL = 0x11
VK_C = 0x43
KEYEVENTF_KEYUP = 0x0002
CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002


# ---------- 剪贴板 ----------
def set_clipboard_text(text: str) -> bool:
    user32.OpenClipboard.argtypes = [wt.HWND]
    user32.SetClipboardData.argtypes = [wt.UINT, wt.HANDLE]
    user32.SetClipboardData.restype = wt.HANDLE
    kernel32.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
    kernel32.GlobalAlloc.restype = wt.HGLOBAL
    kernel32.GlobalLock.argtypes = [wt.HGLOBAL]
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalUnlock.argtypes = [wt.HGLOBAL]

    data = text.encode("utf-16-le") + b"\x00\x00"
    h = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
    if not h:
        return False

    p = kernel32.GlobalLock(h)
    if not p:
        return False
    ctypes.memmove(p, data, len(data))
    kernel32.GlobalUnlock(h)

    if not user32.OpenClipboard(None):
        return False
    try:
        user32.EmptyClipboard()
        user32.SetClipboardData(CF_UNICODETEXT, h)
    finally:
        user32.CloseClipboard()
    return True


def clipboard_seq() -> int:
    user32.GetClipboardSequenceNumber.restype = ctypes.c_uint
    return user32.GetClipboardSequenceNumber()


# ---------- 模拟按键 ----------
def press_ctrl_c() -> None:
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_C, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_C, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0)


# ---------- 截图 ----------
def screenshot(name: str) -> str | None:
    try:
        from PIL import ImageGrab
    except ImportError:
        print(f"  (跳过截图 {name}：没装 pillow)")
        return None

    os.makedirs(SHOT_DIR, exist_ok=True)
    path = os.path.join(SHOT_DIR, name)

    try:
        img = ImageGrab.grab()
    except Exception as e:
        print(f"  (截图失败：{e})")
        return None

    # 提示窗在屏幕底部中央，裁出底部 260px 便于看清
    w, h = img.size
    crop = img.crop((0, max(0, h - 260), w, h))
    crop.save(path)
    print(f"  截图已保存: {path}  (整屏 {w}x{h})")
    return path


def is_running() -> bool:
    out = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq CopyCheck.exe", "/NH"],
        capture_output=True, text=True, errors="ignore"
    ).stdout
    return "CopyCheck.exe" in out


def error_log() -> str:
    p = os.path.join(os.environ.get("APPDATA", ""), "CopyCheck", "error.log")
    if os.path.exists(p):
        with open(p, "r", encoding="utf-8", errors="ignore") as f:
            return f.read()[-1500:]
    return ""


def main() -> int:
    exe = os.path.abspath(EXE)
    print(f"=== CopyCheck 端到端测试 ===")
    print(f"exe: {exe}")
    print(f"存在: {os.path.exists(exe)}")
    print()

    if not os.path.exists(exe):
        print("找不到 exe，先 build")
        return 1

    # 先确保没有残留实例
    subprocess.run(["taskkill", "/F", "/IM", "CopyCheck.exe"],
                   capture_output=True)

    print("[1] 启动程序")
    env = os.environ.copy()
    env["COPYCHECK_TRACE"] = "1"
    proc = subprocess.Popen(
        [exe],
        creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
        close_fds=True,
        env=env,
    )
    time.sleep(3.0)

    alive = is_running()
    print(f"  进程存活: {alive}")
    if not alive:
        print("  启动失败，错误日志:")
        print(error_log() or "  (无日志)")
        return 1

    log = error_log()
    if log:
        print(f"  错误日志有内容:\n{log}")

    # ---------- 场景 A：按了 Ctrl+C，但剪贴板没被写 ----------
    print()
    print("[2] 场景A：模拟 Ctrl+C，但剪贴板不写入（应报「未复制成功」）")
    seq_before = clipboard_seq()
    press_ctrl_c()
    time.sleep(3.2)          # 等过扩展窗口 2600ms
    seq_after = clipboard_seq()
    print(f"  序列号 {seq_before} -> {seq_after}（未变说明剪贴板没被写）")
    screenshot("a-failed.png")

    # ---------- 场景 B：按了 Ctrl+C 且剪贴板确实写入 ----------
    print()
    print("[3] 场景B：模拟 Ctrl+C，并在 60ms 后写入剪贴板（应报「已复制」+预览）")
    seq_before = clipboard_seq()
    press_ctrl_c()
    time.sleep(0.06)
    ok = set_clipboard_text("这是 CopyCheck 的端到端测试文本，用来确认预览能正确显示")
    seq_after = clipboard_seq()
    print(f"  写入剪贴板: {ok}，序列号 {seq_before} -> {seq_after}")
    time.sleep(0.6)
    screenshot("b-success.png")

    # ---------- 场景 C：内容完全相同 ----------
    print()
    print("[4] 场景C：再复制一次完全相同的文本（应报「内容未变化」）")
    press_ctrl_c()
    time.sleep(0.06)
    set_clipboard_text("这是 CopyCheck 的端到端测试文本，用来确认预览能正确显示")
    time.sleep(0.6)
    screenshot("c-unchanged.png")

    print()
    print("=== 测试结束 ===")
    print("程序仍在托盘运行，可以手动复制试试。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
