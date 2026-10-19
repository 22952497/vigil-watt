"""零写入探针：打开 S9 鼠标 0xFF06 接口并被动监听 input report。

用途：确认官方 WebHID 实际使用的通信通道（0xFF06 的 out/in report 0x09）
在本机 hidapi 下能否打开、是否会有自发推送。不发送任何帧。
"""
import sys
import time

import hid

TARGET_VID = 0x372E
TARGET_PID = 0x1026


def fmt(bs, n=40):
    return " ".join(f"{b:02X}" for b in bs[:n])


def main():
    seconds = 8
    for arg in sys.argv[1:]:
        if arg.startswith("--seconds="):
            seconds = int(arg.split("=", 1)[1])

    targets = [d for d in hid.enumerate()
               if d["vendor_id"] == TARGET_VID and d["product_id"] == TARGET_PID
               and d["usage_page"] == 0xFF06 and d["usage"] == 0x0001]
    print(f"0xFF06/0x0001 接口 {len(targets)} 个")
    if not targets:
        return 1

    for d in targets:
        print(f"  path = {d['path']}")
        print(f"  interface={d.get('interface_number')} product={d.get('product_string')}")
        dev = hid.device()
        try:
            dev.open_path(d["path"])
        except Exception as exc:
            print(f"  [打不开] {exc}")
            continue
        try:
            dev.set_nonblocking(1)
            print(f"  已打开，被动监听 {seconds} 秒（零写入）...")
            deadline = time.time() + seconds
            frames = 0
            while time.time() < deadline:
                resp = dev.read(64)
                if not resp:
                    time.sleep(0.01)
                    continue
                frames += 1
                print(f"  frame#{frames}: {fmt(resp)} ({len(resp)}B)")
            print(f"  ==> {seconds} 秒内收到 {frames} 帧")
        finally:
            dev.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
