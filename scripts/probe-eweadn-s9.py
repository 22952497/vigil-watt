"""探针：EWEADN S9 鼠标（372E:1026）feature report 电量读取。

协议来源（官方 WebHID 实现，https://hub.eweadn.cn/by/web）：
  通道   featureReportId = 0x03，长度 63
  帧头   [0] = 校验和 = sum([1..62]) & 0xFF
         [1] = 主命令，[2] = 子命令，[3] = 参数，[4] = 设备类型
  电量   官方 getBasics 走 request(0x4F, 0x81)，响应 [6]=电量(|0x80=充电中)

用法：
    python probe-eweadn-s9.py             # 只读：枚举 + 读现有 feature 报告
    python probe-eweadn-s9.py --send      # 发送 1 帧官方电量查询（需用户授权）
    python probe-eweadn-s9.py --dump      # 打印原始字节
"""
import sys
import time

import hid

TARGET_VID = 0x372E
TARGET_PID = 0x1026
FEATURE_REPORT_ID = 0x03
PACKET_LEN = 63
USAGE_PAGE_VENDOR = 0xFF00
USAGE_VENDOR = 0x0001


def find_targets():
    """按 usagePage/usage 定位厂商接口（官方只认 0xFF00/0x0001）。"""
    out = []
    for d in hid.enumerate():
        if d["vendor_id"] != TARGET_VID or d["product_id"] != TARGET_PID:
            continue
        if d["usage_page"] == USAGE_PAGE_VENDOR and d["usage"] == USAGE_VENDOR:
            out.append(d)
    return out


def build_frame(main_cmd, sub_cmd, param=0, dev_type=0):
    """按官方 LR.generateView 结构构造 63 字节帧；[0] 是校验和。"""
    frame = bytearray(PACKET_LEN)
    frame[1] = main_cmd
    frame[2] = sub_cmd
    frame[3] = param
    frame[4] = dev_type
    frame[0] = sum(frame[1:]) & 0xFF
    return bytes(frame)


def fmt(bs, n=24):
    return " ".join(f"{b:02X}" for b in bs[:n])


def main():
    dump = "--dump" in sys.argv
    do_send = "--send" in sys.argv

    targets = find_targets()
    print(f"命中厂商接口 {len(targets)} 个")
    if not targets:
        print("未找到 372E:1026 usagePage=0xFF00/usage=0x0001 接口")
        return 1

    for d in targets:
        path = d["path"]
        print(f"\n接口 IF{d.get('interface_number', -1)}  {path[-56:]}")
        try:
            dev = hid.device()
            dev.open_path(path)
        except Exception as exc:
            print(f"  [跳过] 打不开: {exc}")
            continue

        try:
            try:
                cur = dev.get_feature_report(FEATURE_REPORT_ID, PACKET_LEN)
                print(f"  [只读] feature 0x03 返回 {len(cur)} 字节")
                if dump and cur:
                    print(f"  raw: {fmt(cur)}")
            except Exception as exc:
                print(f"  [只读] get_feature_report 失败: {exc}")

            if do_send:
                frame = build_frame(0x4F, 0x81)
                print(f"  发送电量查询: {fmt(frame, 8)} ...")
                n = dev.send_feature_report(bytes([FEATURE_REPORT_ID]) + frame)
                print(f"  send_feature_report 返回 {n}")
                time.sleep(0.15)
                resp = dev.get_feature_report(FEATURE_REPORT_ID, PACKET_LEN)
                print(f"  响应 {len(resp)} 字节: {fmt(resp)}")
                if len(resp) > 6:
                    raw = resp[6]
                    print(f"  -> 电量 {raw & 0x7F}%  充电中={bool((raw >> 7) & 1)}")
        finally:
            dev.close()

    if not do_send:
        print("\n（只读模式，未发送任何写入帧）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
