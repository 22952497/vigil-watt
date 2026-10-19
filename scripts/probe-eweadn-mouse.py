"""EWEADN S9 鼠标探针（零写入）。读取 feature report 原始内容，供协议比对。
功能：dump 指定 VID/PID 的 feature report，打印完整字节。
创建时间：2026/10/18 13:12:00 Admin
"""
import argparse

import hid

VID, PID = 0x372E, 0x1026


def find(usage_page=None, usage=None):
    out = []
    for d in hid.enumerate():
        if d["vendor_id"] != VID or d["product_id"] != PID:
            continue
        if usage_page is not None and d.get("usage_page") != usage_page:
            continue
        if usage is not None and d.get("usage") != usage:
            continue
        out.append(d)
    return out


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", default="feat", choices=["enum", "feat"])
    ap.add_argument("--page", type=lambda s: int(s, 0), default=0xFF00)
    ap.add_argument("--usage", type=lambda s: int(s, 0), default=0x0001)
    ap.add_argument("--rid", type=lambda s: int(s, 0), default=0x03)
    ap.add_argument("--len", type=int, default=64)
    a = ap.parse_args()

    devs = find(a.page, a.usage)
    print(f"matched {len(devs)} interface(s) for {a.page:#06x}/{a.usage:#06x}")
    for d in devs:
        print("  ", d["path"].decode(errors="replace") if isinstance(d["path"], bytes) else d["path"])
        print("   product=", d.get("product_string"), "mfr=", d.get("manufacturer_string"))
    if a.mode == "enum" or not devs:
        return

    for d in devs:
        try:
            h = hid.device()
            h.open_path(d["path"])
        except Exception as e:
            print("open failed:", e)
            continue
        try:
            for ln in (a.len, a.len + 1, 63, 65):
                try:
                    r = h.get_feature_report(a.rid, ln)
                except Exception as e:
                    print(f"  len={ln} -> error {e}")
                    continue
                print(f"  len={ln} -> {len(r)}B: {hexs(r)}")
        finally:
            h.close()


if __name__ == "__main__":
    main()
