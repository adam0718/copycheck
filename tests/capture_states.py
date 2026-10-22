"""
用 --demo 模式逐个渲染六种状态并抓图。

为什么不走真实按键：测试环境的前台窗口会抢先响应 Ctrl+C（往剪贴板写空内容），
导致「复制成功」和「复制失败」两个场景都无法稳定复现。
--demo 模式直接构造结果对象，只验证渲染层，稳定可重复。

真实交互路径由 tests/capture_actions.py 单独覆盖。
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

user32 = ctypes.windll.user32
SHOT_DIR = os.path.join(os.path.dirname(__file__), "shots")

STATES = [
    ("state-copy.png",      "copy",      "已复制"),
    ("state-cut.png",       "cut",       "已剪切"),
    ("state-paste.png",     "paste",     "已粘贴"),
    ("state-unchanged.png", "unchanged", "内容未变化"),
    ("state-empty.png",     "empty",     "内容是空的"),
    ("state-failed.png",    "failed",    "未复制成功"),
]


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


def kill():
    subprocess.run(["taskkill", "/F", "/IM", "CopyCheck.exe"], capture_output=True)
    time.sleep(0.4)


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

    if not user32.PrintWindow(hwnd, hdc_mem, 2):
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

    exe = os.environ.get("COPYCHECK_EXE") or os.path.join(
        os.path.dirname(__file__), "..", "dist", "CopyCheck.exe")
    exe = os.path.abspath(exe)

    if not os.path.exists(exe):
        print(f"找不到 exe: {exe}")
        return 1

    ok = 0
    for filename, mode, label in STATES:
        kill()
        print(f"--- {label} (--demo={mode}) ---")

        subprocess.Popen([exe, f"--demo={mode}"],
                         creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
                         close_fds=True)
        time.sleep(1.6)

        pid = find_pid()
        if not pid:
            print("  进程没起来")
            continue

        wins = find_toast(pid)
        if not wins:
            print("  没找到提示窗")
            continue

        hwnd, w, h = wins[0]
        capture(hwnd, w, h, os.path.join(SHOT_DIR, filename))
        print(f"  已抓取 {filename} ({w}x{h})")
        ok += 1

    kill()
    print(f"\n完成 {ok}/{len(STATES)} 个状态")
    return 0 if ok == len(STATES) else 1


if __name__ == "__main__":
    sys.exit(main())
