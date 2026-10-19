# 一次性探针：完整解析 HID 报告描述符，按 report ID 汇总方向与位宽（纯读，零写入）
import sys
import hid

MAIN = {0x8: "Input", 0x9: "Output", 0xB: "Feature"}
GLOBAL = {0x0: "UsagePage", 0x1: "LogMin", 0x2: "LogMax", 0x7: "ReportSize",
          0x8: "ReportID", 0x9: "ReportCount", 0xA: "Push", 0xB: "Pop"}
LOCAL = {0x0: "Usage", 0x1: "UsageMin", 0x2: "UsageMax"}


def parse(desc):
    """解析描述符，返回 {(report_id, kind): 总位数} 与 usage 摘要。"""
    acc = {}
    stack = []
    g = {"ReportSize": 0, "ReportCount": 0, "ReportID": 0, "UsagePage": 0}
    usages = []
    i = 0
    while i < len(desc):
        b = desc[i]
        if b == 0xFE:  # long item
            if i + 2 >= len(desc):
                break
            size = desc[i + 1]
            i += 3 + size
            continue
        size = b & 0x03
        if size == 3:
            size = 4
        btype = (b >> 2) & 0x03
        btag = (b >> 4) & 0x0F
        val = 0
        for k in range(size):
            if i + 1 + k < len(desc):
                val |= desc[i + 1 + k] << (8 * k)
        i += 1 + size
        if btype == 1:  # global
            name = GLOBAL.get(btag)
            if name == "Push":
                stack.append(dict(g))
            elif name == "Pop":
                if stack:
                    g = stack.pop()
            elif name:
                g[name] = val
        elif btype == 2:  # local
            if btag == 0x0:
                usages.append((g.get("UsagePage", 0), val))
        elif btype == 0:  # main
            if btag in MAIN:
                rid = g.get("ReportID", 0)
                bits = g.get("ReportSize", 0) * g.get("ReportCount", 0)
                key = (rid, MAIN[btag])
                acc[key] = acc.get(key, 0) + bits
                usages = []
            elif btag == 0xA:
                usages = []
    return acc


def main():
    vid = int(sys.argv[1], 16) if len(sys.argv) > 1 else 0x372E
    pid = int(sys.argv[2], 16) if len(sys.argv) > 2 else None
    for d in hid.enumerate():
        if d["vendor_id"] != vid:
            continue
        if pid is not None and d["product_id"] != pid:
            continue
        path = d["path"]
        print("=" * 78)
        print(f"{d['vendor_id']:04X}:{d['product_id']:04X}  MI={d.get('interface_number')}  "
              f"{d.get('product_string')}")
        print(f"  path={path}")
        try:
            h = hid.device()
            h.open_path(path)
            desc = None
            for n in (4096, 16384, 65536):
                try:
                    got = h.get_report_descriptor(n)
                except Exception:
                    got = None
                if got:
                    desc = bytes(got)
                    break
            h.close()
        except Exception as e:
            print(f"  open/desc error: {e}")
            continue
        if not desc:
            print("  descriptor unavailable")
            continue
        print(f"  descriptor {len(desc)} bytes")
        acc = parse(desc)
        if not acc:
            print("  (no input/output/feature items)")
        for (rid, kind), bits in sorted(acc.items()):
            print(f"    id=0x{rid:02X}  {kind:7s}  {bits:5d} bits = {bits // 8:3d} bytes")


main()
