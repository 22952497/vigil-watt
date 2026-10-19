"""EWEADN 无线键鼠 HID 接口枚举 + 被动监听探针。

只读：不向设备写任何字节，仅打开接口并等待自发推送。
用法：python probe-eweadn.py [passive|enum]
"""
import sys
import time
import hid

VIDS = [0x0C45, 0x372E]


def enum_all():
    for vid in VIDS:
        print(f"===== VID {vid:04X} =====")
        try:
            devices = hid.enumerate(vid)
        except Exception as exc:  # noqa: BLE001
            print(f"  enumerate failed: {exc}")
            continue
        for d in devices:
            path = d["path"]
            if isinstance(path, bytes):
                path = path.decode("utf-8", "replace")
            print(
                f"  MI={d.get('interface_number')} "
                f"usage_page=0x{d.get('usage_page', 0):04X} "
                f"usage=0x{d.get('usage', 0):04X} "
                f"in={d.get('input_report_length')} "
                f"out={d.get('output_report_length')} "
                f"feat={d.get('feature_report_length')} "
                f"| {d.get('product_string')} / {d.get('manufacturer_string')}"
            )
            print(f"      {path}")


def passive(seconds=8.0):
    """打开每个 vendor 接口，被动读报告，不写任何字节。"""
    targets = []
    for vid in VIDS:
        for d in hid.enumerate(vid):
            up = d.get("usage_page", 0)
            if up >= 0xFF00:
                targets.append(d)

    if not targets:
        print("没有找到 vendor 接口")
        return

    handles = []
    for d in targets:
        path = d["path"]
        label = (
            f"{d.get('product_string')} MI={d.get('interface_number')} "
            f"up=0x{d.get('usage_page', 0):04X} u=0x{d.get('usage', 0):04X} "
            f"in={d.get('input_report_length')}"
        )
        try:
            h = hid.device()
            h.open_path(path)
            h.set_nonblocking(0)
            handles.append((label, h))
            print(f"[open] {label}")
        except Exception as exc:  # noqa: BLE001
            print(f"[fail] {label}: {exc}")

    if not handles:
        return

    print(f"\n被动监听 {seconds:.0f} 秒（零写入）… 请在此期间敲击键盘 / 移动鼠标 / 按键盘组合键切模式")
    deadline = time.time() + seconds
    counts = {}
    while time.time() < deadline:
        for label, h in handles:
            try:
                data = h.read(64, timeout_ms=120)
            except Exception:  # noqa: BLE001
                continue
            if data:
                counts[label] = counts.get(label, 0) + 1
                hexs = " ".join(f"{b:02X}" for b in data[:24])
                print(f"[rx] {label} len={len(data)}: {hexs}")

    print("\n===== 汇总 =====")
    for label, _ in handles:
        print(f"  {counts.get(label, 0):4d} 帧  {label}")
    for _, h in handles:
        try:
            h.close()
        except Exception:  # noqa: BLE001
            pass


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "enum"
    if mode == "passive":
        passive(float(sys.argv[2]) if len(sys.argv) > 2 else 8.0)
    else:
        enum_all()
