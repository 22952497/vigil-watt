"""探针：读取候选厂商接口的 feature report（纯读，零写入）。

安全说明：GetFeature 是 HID 规范里的**读**操作，不改变设备状态，
也不属于 safety.md 禁止的"命令枚举/ID 扫描"——这里只读描述符已声明的那个
feature report ID，不发任何 output 报文。

用法：
    python probe-hid-feature.py
"""
import hid

# (vid, pid, interface_number, usage_page, usage, feature_report_id, length)
TARGETS = [
    (0x372E, 0x1026, 2, 0xFF00, 0x0001, 0x03, 63),
    (0x048D, 0xC103, 0, 0xFF89, 0x0010, 0x5A, 16),
]


def find_path(vid, pid, ifnum, upage, usage):
    for d in hid.enumerate(vid, pid):
        if (d.get("interface_number") == ifnum
                and d["usage_page"] == upage and d["usage"] == usage):
            return d["path"]
    return None


def main():
    for vid, pid, ifnum, upage, usage, rid, length in TARGETS:
        tag = f"{vid:04X}:{pid:04X}/IF{ifnum}/{upage:04X}:{usage:04X} feature id={rid:#04x}"
        path = find_path(vid, pid, ifnum, upage, usage)
        if path is None:
            print(f"[{tag}] 接口不在位")
            continue
        try:
            dev = hid.device()
            dev.open_path(path)
        except Exception as exc:
            print(f"[{tag}] 打不开: {exc}")
            continue
        try:
            data = dev.get_feature_report(rid, length)
            if not data:
                print(f"[{tag}] 无数据")
            else:
                print(f"[{tag}] {len(data)}B  {bytes(data).hex(' ')}")
        except Exception as exc:
            print(f"[{tag}] 读失败: {exc}")
        finally:
            try:
                dev.close()
            except Exception:
                pass


if __name__ == "__main__":
    main()
