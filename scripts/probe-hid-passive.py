"""探针：对候选 HID 接口做零写入被动监听，记录原始报文。

安全：只 open + read，不发送任何报文（docs/hardware/safety.md 的"被动优先"）。

用途：判断哪些厂商接口在主动说话、报文长度与内容长什么样，
     为后续定位"电量字段在第几个字节"提供原始样本。

用法：
    python probe-hid-passive.py                 # 默认 20 秒
    python probe-hid-passive.py --seconds 60
    python probe-hid-passive.py --vid 372E      # 只看某个 VID
"""
import sys
import threading
import time

import hid

# 候选：本机 2026-10-18 枚举出的厂商/可疑接口（VID, PID, interface_number, usage_page, usage）
TARGETS = [
    (0x0C45, 0xFEFE, 2, 0xFFFF, 0x0001),  # in 3B id=0x05
    (0x0C45, 0xFEFE, 3, 0xFF60, 0x0061),  # QMK raw HID 32B
    (0x0C45, 0xFEFE, 4, 0xFF60, 0x0061),  # QMK raw HID 64B
    (0x372E, 0x1026, 2, 0xFF06, 0x0001),  # in 31B id=0x09
    (0x048D, 0xC103, 0, 0xFF89, 0x0010),  # feature-only，被动听不到东西
    (0x3302, 0x1233, 3, 0xFF01, 0x0001),  # in 63B id=0x4b/0x54
]

STOP = threading.Event()
OUT_LOCK = threading.Lock()


def find_path(vid, pid, ifnum, upage, usage):
    for d in hid.enumerate(vid, pid):
        if (d.get("interface_number") == ifnum
                and d["usage_page"] == upage and d["usage"] == usage):
            return d["path"]
    return None


def listen(vid, pid, ifnum, upage, usage, seconds, stats):
    path = find_path(vid, pid, ifnum, upage, usage)
    tag = f"{vid:04X}:{pid:04X}/IF{ifnum}/{upage:04X}:{usage:04X}"
    if path is None:
        stats[tag] = ("接口不在位", [])
        return
    try:
        dev = hid.device()
        dev.open_path(path)
    except Exception as exc:
        stats[tag] = (f"打不开: {exc}", [])
        return

    frames = []
    deadline = time.time() + seconds
    try:
        dev.set_nonblocking(0)
        while not STOP.is_set() and time.time() < deadline:
            data = dev.read(64, timeout_ms=200)
            if data:
                frames.append(bytes(data))
                if len(frames) <= 8:  # 只留前 8 帧样本，避免刷屏
                    with OUT_LOCK:
                        print(f"[{tag}] {len(data):>2}B  {bytes(data).hex(' ')}")
    except Exception as exc:
        stats[tag] = (f"读取出错: {exc}", frames)
        return
    finally:
        try:
            dev.close()
        except Exception:
            pass

    if not frames:
        stats[tag] = ("静默（无报文）", [])
    else:
        # 统计不同报文种类
        uniq = {}
        for f in frames:
            uniq[bytes(f)] = uniq.get(bytes(f), 0) + 1
        stats[tag] = (f"{len(frames)} 帧 / {len(uniq)} 种", frames)


def main():
    argv = sys.argv[1:]
    seconds = 20
    vids = None
    for i, a in enumerate(argv):
        if a == "--seconds" and i + 1 < len(argv):
            seconds = int(argv[i + 1])
        if a == "--vid" and i + 1 < len(argv):
            vids = {int(argv[i + 1], 16)}

    targets = [t for t in TARGETS if vids is None or t[0] in vids]
    print(f"被动监听 {len(targets)} 个接口，{seconds} 秒，零写入…\n")

    stats = {}
    threads = []
    for t in targets:
        th = threading.Thread(target=listen, args=(*t, seconds, stats), daemon=True)
        th.start()
        threads.append(th)

    try:
        for th in threads:
            th.join()
    except KeyboardInterrupt:
        STOP.set()

    print("\n=== 汇总 ===")
    for t in targets:
        tag = f"{t[0]:04X}:{t[1]:04X}/IF{t[2]}/{t[3]:04X}:{t[4]:04X}"
        print(f"  {tag:<32} {stats.get(tag, ('未开始', []))[0]}")


if __name__ == "__main__":
    main()
