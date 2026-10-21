"""
验证 CopyCheck 的核心判据：剪贴板序列号能否可靠区分「写入了」和「没写入」。

这是整个产品的地基。如果这个判据不成立，工具就没有意义。
"""

import ctypes
import time
import tkinter

user32 = ctypes.windll.user32
user32.GetClipboardSequenceNumber.restype = ctypes.c_uint


def seq() -> int:
    return user32.GetClipboardSequenceNumber()


def set_clipboard(text: str) -> None:
    root = tkinter.Tk()
    root.withdraw()
    root.clipboard_clear()
    root.clipboard_append(text)
    root.update()          # 必须 update 才会真正写进系统剪贴板
    time.sleep(0.05)
    root.destroy()


def check(label: str, before: int, after: int, expect_change: bool) -> bool:
    changed = after != before
    ok = changed == expect_change
    mark = "PASS" if ok else "FAIL"
    delta = after - before
    detail = f"序列号 {before} -> {after} (变化 {delta:+d})"
    print(f"[{mark}] {label}: {detail}")
    return ok


print("=== 剪贴板序列号判据验证 ===")
print()

s0 = seq()
print(f"初始序列号: {s0}")
print()

results = []

# 1. 什么都不做，序列号不应该变
time.sleep(0.4)
s1 = seq()
results.append(check("空闲 400ms，序列号应保持不变", s0, s1, expect_change=False))

# 2. 写入内容，序列号必须变
set_clipboard("CopyCheck 判据测试 A")
s2 = seq()
results.append(check("写入文本 A，序列号必须递增", s1, s2, expect_change=True))

# 3. 写入不同内容，序列号必须变
set_clipboard("CopyCheck 判据测试 B")
s3 = seq()
results.append(check("写入文本 B，序列号必须递增", s2, s3, expect_change=True))

# 4. 关键边界：写入完全相同的内容
set_clipboard("CopyCheck 判据测试 B")
s4 = seq()
results.append(check("重复写入相同文本 B，序列号仍应递增", s3, s4, expect_change=True))

print()
print("--- 结论 ---")
if all(results):
    print("判据成立：")
    print("  序列号不变 == 剪贴板从未被写入 == 复制确实失败了。")
    print("  序列号变了 == 剪贴板被写入过（即使内容与上次相同）。")
    print("所以「按了 Ctrl+C 但序列号不动」可以确定地判定为复制失败，不会误判。")
else:
    print("判据存在偏差，需要重新设计检测方式。")

print()
print(f"最终序列号: {seq()}")
