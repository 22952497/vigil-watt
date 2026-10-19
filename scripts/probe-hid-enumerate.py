"""探针：枚举本机 HID 接口，打印 VID/PID/usage/报告长度。

只做枚举与元数据读取，不发送任何报文（零写入）。
用途：为"快捷查看键鼠电量"确定每个候选 dongle 上有哪些 collection、
以及哪个 collection 可能是电量通道。

用法：
    python probe-hid-enumerate.py                # 只列目标 VID
    python probe-hid-enumerate.py --all          # 列全部 HID
    python probe-hid-enumerate.py --vid 0C45     # 指定 VID（可多次）
"""
import sys

import hid

# 本机在用键鼠相关 VID（2026-10-18 实测）
DEFAULT_VIDS = [0x0C45, 0x048D, 0x372E, 0x3302]


def parse_args(argv):
    vids = []
    show_all = "--all" in argv
    i = 0
    while i < len(argv):
        if argv[i] == "--vid" and i + 1 < len(argv):
            vids.append(int(argv[i + 1], 16))
            i += 2
            continue
        i += 1
    return show_all, vids or DEFAULT_VIDS


def main():
    show_all, vids = parse_args(sys.argv[1:])
    rows = hid.enumerate()

    def wanted(d):
        return show_all or d["vendor_id"] in vids

    sel = [d for d in rows if wanted(d)]
    sel.sort(key=lambda d: (d["vendor_id"], d["product_id"],
                            d.get("interface_number") or 0,
                            d["usage_page"], d["usage"]))

    print(f"HID 接口总数 {len(rows)}，命中 {len(sel)}\n")
    hdr = f"{'VID:PID':<10} {'IF':>3} {'usagePage':>9} {'usage':>6} {'product':<24} path"
    print(hdr)
    print("-" * len(hdr))
    for d in sel:
        path = d["path"].decode(errors="replace")
        # 只保留末尾的 collection 标识，避免整行过长
        short = path.split("&")[-1] if "&" in path else path
        print(f"{d['vendor_id']:04X}:{d['product_id']:04X} "
              f"{d.get('interface_number', -1):>3} "
              f"{d['usage_page']:#09x} {d['usage']:#06x} "
              f"{(d.get('product_string') or '')[:24]:<24} {short}")


if __name__ == "__main__":
    main()
