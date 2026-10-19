"""探针：EWEADN S9 鼠标（372E:1026）官方 BASICS 电量帧。

协议来源（官方 WebHID，https://hub.eweadn.cn/by/web 的 by-web.js）：
  命令表 wn.BASICS = [33, 80, 0, 2, 79, 129]
  generateView：63 字节缓冲，把命令数组写进 [0..5]，再 generateCRC
  generateCRC：[0] = sum([1..62]) & 0xFF
  => 实发 [0]=0x22 [1]=0x50 [2]=0x00 [3]=0x02(数据长度) [4]=0x4F [5]=0x81
  decodeGetBasics：[6] bit7=充电中，低 7 位=电量%

两条候选通道（描述符决定，需实测确认）：
  A. 0xFF06/0x0001 接口：output report 0x09 (63B) 发，input report 0x09 (31B) 收
  B. 0xFF00/0x0001 接口：feature report 0x03 (63B) 收发

用法（都会发帧，需用户授权）：
    python probe-eweadn-s9-basics.py --output    # 通道 A
    python probe-eweadn-s9-basics.py --feature   # 通道 B
    python probe-eweadn-s9-basics.py             # 只读：不发送任何帧
"""
import sys
import time

import hid

TARGET_VID = 0x372E
TARGET_PID = 0x1026
PACKET_LEN = 63
FEATURE_REPORT_ID = 0x03
OUT_REPORT_ID = 0x09
IN_REPORT_ID = 0x09


def build_basics_frame():
    """按 wn.BASICS + generateCRC 构造 63 字节查询帧。"""
    frame = bytearray(PACKET_LEN)
    frame[1] = 0x50
    frame[2] = 0x00
    frame[3] = 0x02
    frame[4] = 0x4F
    frame[5] = 0x81
    frame[0] = sum(frame[1:]) & 0xFF
    return bytes(frame)


def find_by_usage(usage_page, usage):
    return [d for d in hid.enumerate()
            if d["vendor_id"] == TARGET_VID and d["product_id"] == TARGET_PID
            and d["usage_page"] == usage_page and d["usage"] == usage]


def fmt(bs, n=32):
    return " ".join(f"{b:02X}" for b in bs[:n])


def dump_bytes(label, bs):
    print(f"  {label}: {fmt(bs)} ({len(bs)}B)")


def channel_output(frame):
    """通道 A：0xFF06 接口的 output report 0x09，响应走 input report 0x09。"""
    targets = find_by_usage(0xFF06, 0x0001)
    print(f"[A] 0xFF06/0x0001 接口 {len(targets)} 个")
    for d in targets:
        dev = hid.device()
        try:
            dev.open_path(d["path"])
        except Exception as exc:
            print(f"  [跳过] 打不开: {exc}")
            continue
        try:
            dev.set_nonblocking(1)
            n = dev.write(bytes([OUT_REPORT_ID]) + frame)
            print(f"  write(out 0x09) 返回 {n}")
            deadline = time.time() + 2.0
            seen = 0
            while time.time() < deadline:
                resp = dev.read(64)
                if not resp:
                    time.sleep(0.02)
                    continue
                seen += 1
                dump_bytes(f"input#{seen}", resp)
                if len(resp) > 7:
                    body = resp[1:] if resp[0] == IN_REPORT_ID else resp
                    if len(body) > 6:
                        raw = body[6]
                        print(f"    -> body[6]=0x{raw:02X} 电量={raw & 0x7F}% 充电中={bool((raw >> 7) & 1)}")
            if seen == 0:
                print("  2 秒内没有收到任何 input report")
        finally:
            dev.close()


def channel_feature(frame):
    """通道 B：0xFF00 接口的 feature report 0x03 收发。"""
    targets = find_by_usage(0xFF00, 0x0001)
    print(f"[B] 0xFF00/0x0001 接口 {len(targets)} 个")
    for d in targets:
        dev = hid.device()
        try:
            dev.open_path(d["path"])
        except Exception as exc:
            print(f"  [跳过] 打不开: {exc}")
            continue
        try:
            before = dev.get_feature_report(FEATURE_REPORT_ID, PACKET_LEN + 1)
            dump_bytes("发帧前 feature 0x03", before)
            n = dev.send_feature_report(bytes([FEATURE_REPORT_ID]) + frame)
            print(f"  send_feature_report(0x03) 返回 {n}")
            time.sleep(0.2)
            after = dev.get_feature_report(FEATURE_REPORT_ID, PACKET_LEN + 1)
            dump_bytes("发帧后 feature 0x03", after)
            # 官方 decodeGetBasics 直接读含 report ID 的原始响应的 t[6]，不剥首字节
            if len(after) > 12:
                raw = after[6]
                max_dpi = after[10] + after[11] * 255
                print(f"    -> t[6]=0x{raw:02X} 电量={raw & 0x7F}% 充电中={bool((raw >> 7) & 1)}")
                print(f"       t[7]=0x{after[7]:02X} profile={after[7] & 15}/{after[7] >> 4}"
                      f"  t[8]=0x{after[8]:02X}  t[9]=0x{after[9]:02X}"
                      f"  最大DPI={max_dpi}  t[12]=0x{after[12]:02X}")
        finally:
            dev.close()


def read_only():
    for page, usage, label in ((0xFF00, 0x0001, "0xFF00"), (0xFF06, 0x0001, "0xFF06")):
        for d in find_by_usage(page, usage):
            print(f"\n{label}/0x{usage:04X} IF{d.get('interface_number', -1)} ...{d['path'][-24:]}")
            if page != 0xFF00:
                continue
            dev = hid.device()
            try:
                dev.open_path(d["path"])
                dump_bytes("feature 0x03 残留", dev.get_feature_report(FEATURE_REPORT_ID, PACKET_LEN + 1))
            except Exception as exc:
                print(f"  读失败: {exc}")
            finally:
                dev.close()
    print("\n（只读模式，未发送任何帧）")


def main():
    global frame
    frame = build_basics_frame()
    print(f"BASICS 帧(63B): {fmt(frame, 8)} ... [0]=CRC=0x{frame[0]:02X}")

    if "--output" not in sys.argv and "--feature" not in sys.argv:
        read_only()
        return 0
    if "--output" in sys.argv:
        channel_output(frame)
    if "--feature" in sys.argv:
        channel_feature(frame)
    return 0


if __name__ == "__main__":
    sys.exit(main())
