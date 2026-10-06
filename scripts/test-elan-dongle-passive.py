# -*- coding: utf-8 -*-
"""
Elan dongle（04F3:026F，弱水时砂 2.4G 接收器）被动监听——零写入。
监听全部接口的 input report 90 秒。2.4G 模式下若 dongle 推状态/电量帧会在这里现身。
已知：feature 0x05 -> "1010"（固件版本）；电量命令未知；接口地图见 app.log "HID 元数据"。
用法：插上 dongle + 耳机开 2.4G 模式正常使用，跑本脚本看输出。
"""
import hid, time

VID, PID = 0x04F3, 0x026F
DUR = 90.0

def main():
    paths = [d['path'] for d in hid.enumerate(VID, PID)]
    if not paths:
        print("dongle 不在位（04F3:026F 未插入）"); return
    devs = []
    for p in paths:
        h = hid.device()
        try:
            h.open_path(p); h.set_nonblocking(True)
            devs.append((p, h))
            print("listening:", p[-70:])
        except Exception as ex:
            print("open fail:", p[-70:], ex)
    if not devs:
        print("没有任何接口打开成功"); return
    print(f"被动监听 {DUR:.0f}s（期间正常使用耳机：戴/摘/播放/暂停/调音量）...")
    t0 = time.time(); n = 0
    while time.time() - t0 < DUR:
        for i, (p, h) in enumerate(devs):
            data = h.read(64)
            if data:
                n += 1
                ts = time.strftime('%H:%M:%S')
                hexs = ' '.join(f'{b:02X}' for b in data)
                print(f"[{ts}] if{i} ({len(data)}B) {hexs}")
        time.sleep(0.05)
    print(f"共 {n} 帧")
    for _, h in devs: h.close()

if __name__ == '__main__':
    main()
