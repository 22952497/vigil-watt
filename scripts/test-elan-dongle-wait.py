# -*- coding: utf-8 -*-
"""
Elan dongle（04F3:026F）被动监听——等待插入版。
轮询等待 dongle 出现 → 打开全部接口 → 被动收 input report（零写入）。
2.4G 模式下若 dongle 推状态/电量帧会全部记录。
"""
import hid, time, sys

VID, PID = 0x04F3, 0x026F
WAIT_MAX = 1200.0     # 最长等 20 分钟
LISTEN = 300.0        # 出现后监听 5 分钟

def open_all():
    devs = []
    for d in hid.enumerate(VID, PID):
        h = hid.device()
        try:
            h.open_path(d['path']); h.set_nonblocking(True)
            devs.append((d['path'][-60:], d['usage_page'], d['usage'], h))
        except Exception:
            pass
    return devs

def main():
    print(f"等待 04F3:026F 插入（最长 {WAIT_MAX:.0f}s）...", flush=True)
    t0 = time.time()
    devs = []
    while time.time() - t0 < WAIT_MAX:
        devs = open_all()
        if devs:
            break
        time.sleep(1.0)
    if not devs:
        print("超时：dongle 未插入"); return
    for p, up, u, _ in devs:
        print(f"listening: ...{p} usagePage=0x{up:04X} usage=0x{u:04X}", flush=True)
    print(f"被动监听 {LISTEN:.0f}s —— 请正常使用耳机（开2.4G、戴/摘、播放、暂停、调音量、放回充电盒再取出）", flush=True)
    t0 = time.time(); n = 0
    while time.time() - t0 < LISTEN:
        for p, up, u, h in devs:
            data = h.read(64)
            if data:
                n += 1
                ts = time.strftime('%H:%M:%S')
                hexs = ' '.join(f'{b:02X}' for b in data)
                print(f"[{ts}] usage=0x{up:04X}/0x{u:04X} ({len(data)}B) {hexs}", flush=True)
        time.sleep(0.02)
    print(f"共 {n} 帧")
    for _, _, _, h in devs: h.close()

if __name__ == '__main__':
    sys.exit(main())
