"""探针：读取目标 HID 接口的报告描述符，解析顶层集合与电量相关字段。

零写入：只 open + get_report_descriptor + close，不发送任何报文。

关注点（HID 规范）：
    usagePage 0x0006 usage 0x0020 = Battery Strength（标准电量字段）
    usagePage >= 0xFF00            = 厂商自定义集合（电量常藏在这里的 feature report）

用法：
    python probe-hid-descriptor.py            # 全部目标 VID
    python probe-hid-descriptor.py --vid 372E
"""
import sys

import hid

DEFAULT_VIDS = [0x0C45, 0x048D, 0x372E, 0x3302]

# 常见全局项 / 主项标记
ITEM_MAIN_INPUT = 0x80
ITEM_MAIN_OUTPUT = 0x90
ITEM_MAIN_FEATURE = 0xB0
ITEM_MAIN_COLLECTION = 0xA0
ITEM_MAIN_END_COLLECTION = 0xC0
ITEM_GLOBAL_USAGE_PAGE = 0x04
ITEM_GLOBAL_REPORT_ID = 0x84
ITEM_GLOBAL_REPORT_SIZE = 0x74
ITEM_GLOBAL_REPORT_COUNT = 0x94
ITEM_LOCAL_USAGE = 0x08
ITEM_LOCAL_USAGE_MIN = 0x18
ITEM_LOCAL_USAGE_MAX = 0x28


def parse_descriptor(desc: bytes):
    """极简 HID 报告描述符解析：只提取集合与 main 项的 (page, usage, id, size, count)。"""
    items = []
    page = 0
    report_id = 0
    rsize = 0
    rcount = 0
    usages = []
    i = 0
    n = len(desc)
    while i < n:
        prefix = desc[i]
        i += 1
        if prefix == 0xFE:  # long item
            if i + 2 > n:
                break
            dsize = desc[i]
            i += 2 + dsize
            continue
        size = prefix & 0x03
        if size == 3:
            size = 4
        typ = (prefix >> 2) & 0x03
        tag = (prefix >> 4) & 0x0F
        if i + size > n:
            break
        raw = desc[i:i + size]
        i += size
        val = int.from_bytes(raw, "little") if size else 0

        if typ == 0x01:  # global
            if tag == ITEM_GLOBAL_USAGE_PAGE >> 4:
                page = val
            elif tag == ITEM_GLOBAL_REPORT_ID >> 4:
                report_id = val
            elif tag == ITEM_GLOBAL_REPORT_SIZE >> 4:
                rsize = val
            elif tag == ITEM_GLOBAL_REPORT_COUNT >> 4:
                rcount = val
        elif typ == 0x02:  # local
            if tag in (ITEM_LOCAL_USAGE >> 4,):
                usages.append(val)
            elif tag == ITEM_LOCAL_USAGE_MIN >> 4:
                usages.append(("min", val))
            elif tag == ITEM_LOCAL_USAGE_MAX >> 4:
                usages.append(("max", val))
        elif typ == 0x00:  # main
            if tag == ITEM_MAIN_COLLECTION >> 4:
                items.append(("collection", page, usages[0] if usages else 0, report_id, 0, 0))
                usages = []
            elif tag in (ITEM_MAIN_INPUT >> 4, ITEM_MAIN_OUTPUT >> 4, ITEM_MAIN_FEATURE >> 4):
                kind = {ITEM_MAIN_INPUT >> 4: "in",
                        ITEM_MAIN_OUTPUT >> 4: "out",
                        ITEM_MAIN_FEATURE >> 4: "feat"}[tag]
                u = usages[0] if usages else 0
                if isinstance(u, tuple):
                    u = u[1]
                items.append((kind, page, u, report_id, rsize, rcount))
                usages = []
            else:
                usages = []
    return items


def main():
    argv = sys.argv[1:]
    vids = []
    i = 0
    while i < len(argv):
        if argv[i] == "--vid" and i + 1 < len(argv):
            vids.append(int(argv[i + 1], 16))
            i += 2
            continue
        i += 1
    vids = vids or DEFAULT_VIDS

    rows = [d for d in hid.enumerate() if d["vendor_id"] in vids]
    rows.sort(key=lambda d: (d["vendor_id"], d["product_id"],
                             d.get("interface_number") or 0, d["usage_page"]))

    for d in rows:
        label = (f"{d['vendor_id']:04X}:{d['product_id']:04X} "
                 f"IF{d.get('interface_number', -1)} "
                 f"{d['usage_page']:#06x}/{d['usage']:#06x}")
        try:
            dev = hid.device()
            dev.open_path(d["path"])
        except Exception as exc:  # 系统保护接口（键盘/鼠标主集合）打不开属正常
            print(f"=== {label}  打不开: {exc}")
            continue
        try:
            try:
                desc = dev.get_report_descriptor()
            except Exception as exc:
                print(f"=== {label}  读描述符失败: {exc}")
                continue
            items = parse_descriptor(desc)
            print(f"=== {label}  描述符 {len(desc)} 字节")
            for it in items:
                if it[0] == "collection":
                    print(f"    COLLECTION page={it[1]:#06x} usage={it[2]:#06x} id={it[3]}")
                else:
                    kind, page, usage, rid, rsize, rcount = it
                    bits = rsize * rcount
                    flag = ""
                    if page == 0x06 and usage == 0x20:
                        flag = "   <-- Battery Strength"
                    elif page >= 0xFF00:
                        flag = "   <-- vendor"
                    print(f"    {kind:<5} page={page:#06x} usage={usage:#06x} "
                          f"id={rid:#04x} size={rsize} count={rcount} ({bits} bits){flag}")
        finally:
            try:
                dev.close()
            except Exception:
                pass


if __name__ == "__main__":
    main()
