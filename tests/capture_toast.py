"""
直接抓取 CopyCheck 提示窗自身的内容，不受其他窗口遮挡影响。

用 PrintWindow 让窗口把自己画到内存 DC 里，再取像素。
这样验证的是「窗口到底画成了什么样」，而不是「屏幕上碰巧看到什么」。
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD)]


def find_pid() -> int | None:
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq CopyCheck.exe", "/FO", "CSV", "/NH"],
                         capture_output=True).stdout
    text = out.decode("gbk", errors="ignore")
    for line in text.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and "CopyCheck" in parts[0]:
            try:
                return int(parts[1])
            except ValueError:
                return None
    return None


def find_toast(pid: int):
    """找出这个进程里最小的那个可见窗口 —— 提示窗只有 67px 高，托盘隐藏窗口不算。"""
    found = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(hwnd, _):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd):
            r = RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            w, h = r.right - r.left, r.bottom - r.top
            if w > 50 and h > 30:
                found.append((hwnd, w, h, (r.left, r.top, r.right, r.bottom)))
        return True

    user32.EnumWindows(CB(cb), 0)
    found.sort(key=lambda t: t[1] * t[2])
    return found


def capture(hwnd: int, w: int, h: int, out_path: str) -> bool:
    hdc_screen = user32.GetDC(0)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    gdi32.SelectObject(hdc_mem, hbmp)

    # PW_RENDERFULLCONTENT = 2，对分层窗口也能取到内容
    ok = user32.PrintWindow(hwnd, hdc_mem, 2)
    if not ok:
        ok = user32.PrintWindow(hwnd, hdc_mem, 0)

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h          # 负数 = 自上而下
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bi), 0)

    from PIL import Image
    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1)
    img.convert("RGB").save(out_path)

    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(0, hdc_screen)
    return bool(ok)


def press_ctrl_c():
    VK_CONTROL, VK_C, KEYUP = 0x11, 0x43, 0x0002
    user32.keybd_event(VK_CONTROL, 0, 0, 0); time.sleep(0.01)
    user32.keybd_event(VK_C, 0, 0, 0); time.sleep(0.01)
    user32.keybd_event(VK_C, 0, KEYUP, 0); time.sleep(0.01)
    user32.keybd_event(VK_CONTROL, 0, KEYUP, 0)


def set_clipboard(text: str):
    CF_UNICODETEXT, GMEM_MOVEABLE = 13, 0x0002
    kernel32 = ctypes.windll.kernel32
    kernel32.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
    kernel32.GlobalAlloc.restype = wt.HGLOBAL
    kernel32.GlobalLock.argtypes = [wt.HGLOBAL]
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalUnlock.argtypes = [wt.HGLOBAL]
    kernel32.GlobalUnlock.restype = wt.BOOL
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


def main():
    shots = os.path.join(os.path.dirname(__file__), "shots")
    os.makedirs(shots, exist_ok=True)

    pid = find_pid()

    if not pid:
        exe = os.environ.get("COPYCHECK_EXE") or os.path.join(
            os.path.dirname(__file__), "..", "dist", "CopyCheck.exe")
        exe = os.path.abspath(exe)
        print(f"程序未运行，启动: {exe}")
        env = os.environ.copy()
        env["COPYCHECK_TRACE"] = "1"
        subprocess.Popen([exe],
                         creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
                         close_fds=True, env=env)
        time.sleep(2.5)
        pid = find_pid()

    print(f"CopyCheck PID: {pid}")
    if not pid:
        print("启动失败")
        return 1

    scenarios = [
        ("clean-success.png", "成功", "这是用来验证提示窗样式的测试文本，中文英文 mixed content 12345"),
        ("clean-unchanged.png", "未变化", None),
        ("clean-failed.png", "失败", "!no-clipboard!"),
    ]

    for filename, label, payload in scenarios:
        print(f"\n--- 场景: {label} ---")
        press_ctrl_c()
        time.sleep(0.06)

        if payload == "!no-clipboard!":
            # 什么都不写，等它判定失败
            time.sleep(2.9)
        elif payload is None:
            # 写和上一次一样的内容
            set_clipboard("这是用来验证提示窗样式的测试文本，中文英文 mixed content 12345")
            time.sleep(0.45)
        else:
            set_clipboard(payload)
            time.sleep(0.45)

        wins = find_toast(pid)
        if not wins:
            print("  没找到提示窗（可能已经淡出）")
            continue

        hwnd, w, h, rect = wins[0]
        out = os.path.join(shots, filename)
        capture(hwnd, w, h, out)
        print(f"  窗口 rect={rect} size={w}x{h}")
        print(f"  已抓取: {out}")
        time.sleep(2.2)   # 等这次提示消失，避免干扰下一场景

    return 0


if __name__ == "__main__":
    sys.exit(main())
