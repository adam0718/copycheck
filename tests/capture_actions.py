"""
覆盖全部动作的端到端验证。

四种路径的判据完全不同，必须分别验证：
  Ctrl+C  按键 + 剪贴板写入  → 已复制
  Ctrl+X  按键 + 剪贴板写入  → 已剪切
  Ctrl+V  按键 + 不写剪贴板  → 已粘贴（序列号不会变，不能用序列号判据）
  右键复制 无按键 + 剪贴板写入 → 已复制（靠常驻监听捕获）
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

SHOT_DIR = os.path.join(os.path.dirname(__file__), "shots")
VK_CONTROL, VK_C, VK_X, VK_V = 0x11, 0x43, 0x58, 0x56
KEYUP = 0x0002
CF_UNICODETEXT, GMEM_MOVEABLE = 13, 0x0002


def find_pid():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq CopyCheck.exe", "/FO", "CSV", "/NH"],
                         capture_output=True).stdout.decode("gbk", errors="ignore")
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and "CopyCheck" in parts[0]:
            try:
                return int(parts[1])
            except ValueError:
                return None
    return None


def set_clipboard(text):
    kernel32.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
    kernel32.GlobalAlloc.restype = wt.HGLOBAL
    kernel32.GlobalLock.argtypes = [wt.HGLOBAL]
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalUnlock.argtypes = [wt.HGLOBAL]
    user32.SetClipboardData.argtypes = [wt.UINT, wt.HANDLE]
    user32.SetClipboardData.restype = wt.HANDLE

    data = text.encode("utf-16-le") + b"\x00\x00"
    h = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
    p = kernel32.GlobalLock(h)
    ctypes.memmove(p, data, len(data))
    kernel32.GlobalUnlock(h)

    user32.OpenClipboard(None)
    user32.EmptyClipboard()
    user32.SetClipboardData(CF_UNICODETEXT, h)
    user32.CloseClipboard()


def press_ctrl(vk):
    user32.keybd_event(VK_CONTROL, 0, 0, 0); time.sleep(0.01)
    user32.keybd_event(vk, 0, 0, 0); time.sleep(0.01)
    user32.keybd_event(vk, 0, KEYUP, 0); time.sleep(0.01)
    user32.keybd_event(VK_CONTROL, 0, KEYUP, 0)


def find_toast(pid):
    found = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(hwnd, _):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd):
            r = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            w, h = r.right - r.left, r.bottom - r.top
            if 50 < w and 30 < h:
                found.append((hwnd, w, h))
        return True

    user32.EnumWindows(CB(cb), 0)
    found.sort(key=lambda t: t[1] * t[2])
    return found


def capture(hwnd, w, h, out_path):
    gdi32 = ctypes.windll.gdi32
    hdc_screen = user32.GetDC(0)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    gdi32.SelectObject(hdc_mem, hbmp)

    ok = user32.PrintWindow(hwnd, hdc_mem, 2)
    if not ok:
        user32.PrintWindow(hwnd, hdc_mem, 0)

    class BIH(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                    ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                    ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                    ("biClrImportant", wt.DWORD)]

    bi = BIH()
    bi.biSize = ctypes.sizeof(BIH)
    bi.biWidth = w
    bi.biHeight = -h
    bi.biPlanes = 1
    bi.biBitCount = 32

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bi), 0)

    from PIL import Image
    Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB").save(out_path)

    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(0, hdc_screen)


def main():
    os.makedirs(SHOT_DIR, exist_ok=True)

    pid = find_pid()
    if not pid:
        exe = os.environ.get("COPYCHECK_EXE") or os.path.join(
            os.path.dirname(__file__), "..", "dist", "CopyCheck.exe")
        exe = os.path.abspath(exe)
        print(f"启动: {exe}")
        env = os.environ.copy()
        env["COPYCHECK_TRACE"] = "1"
        subprocess.Popen([exe],
                         creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
                         close_fds=True, env=env)
        time.sleep(2.5)
        pid = find_pid()

    if not pid:
        print("启动失败")
        return 1

    print(f"CopyCheck PID: {pid}\n")

    # (文件名, 说明, 按键, 是否写剪贴板, 写入内容)
    scenarios = [
        ("act-copy.png",    "Ctrl+C 复制成功",        VK_C, True,  "这是复制场景的测试文本，用来确认提示标题是「已复制」"),
        ("act-cut.png",     "Ctrl+X 剪切成功",        VK_X, True,  "这是剪切场景的测试文本，用来确认提示标题是「已剪切」"),
        ("act-paste.png",   "Ctrl+V 粘贴",            VK_V, False, None),
        ("act-passive.png", "右键复制（无按键写入）",   None, True,  "这是右键复制的测试文本，模拟菜单里的复制动作"),
        ("act-unchanged.png", "重复复制相同内容",      VK_C, True,  "这是右键复制的测试文本，模拟菜单里的复制动作"),
        ("act-failed.png",  "Ctrl+C 但剪贴板没变",     VK_C, False, None),
    ]

    for filename, label, vk, write, text in scenarios:
        print(f"--- {label} ---")

        if vk is not None:
            press_ctrl(vk)

        if write and text is not None:
            time.sleep(0.06)
            set_clipboard(text)

        # 失败场景要等满扩展窗口，其他场景等提示出现即可
        time.sleep(3.0 if (vk is not None and not write) else 0.5)

        wins = find_toast(pid)
        if not wins:
            print("  没抓到提示窗")
        else:
            hwnd, w, h = wins[0]
            capture(hwnd, w, h, os.path.join(SHOT_DIR, filename))
            print(f"  已抓取 {filename}  ({w}x{h})")

        time.sleep(2.4)   # 等提示消失，避免干扰下一场景

    print("\n=== 全部场景执行完毕 ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
