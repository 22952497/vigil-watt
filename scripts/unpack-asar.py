"""解包 Electron asar 归档（只读，不执行任何被解出的代码）。

asar 布局：
    [0:4]   uint32 = 4（pickle 头长度标记）
    [4:8]   uint32 = header_payload_size
    [8:...] pickle payload: uint32 json_size + JSON 索引 + 对齐填充
    数据区从 8 + header_payload_size 开始

用法：python unpack-asar.py <app.asar> <输出目录> [--list]
"""
import json
import os
import struct
import sys


def read_header(blob: bytes):
    """Chromium pickle 嵌套布局：4 / header_size / json_payload_size / json_len / JSON。"""
    if struct.unpack("<I", blob[0:4])[0] != 4:
        raise ValueError("不是 asar：pickle 标记不匹配")
    header_size = struct.unpack("<I", blob[4:8])[0]
    json_len = struct.unpack("<I", blob[12:16])[0]
    index = json.loads(blob[16:16 + json_len].decode("utf-8"))
    return index, 8 + header_size


def walk(node, prefix=""):
    """产出 (相对路径, 文件信息 dict)。"""
    if "files" in node:
        for name, child in node["files"].items():
            yield from walk(child, f"{prefix}{name}/" if prefix or True else name)
        return
    yield prefix.rstrip("/"), node


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    src, out = sys.argv[1], sys.argv[2]
    list_only = "--list" in sys.argv

    with open(src, "rb") as fh:
        blob = fh.read()

    index, data_start = read_header(blob)
    entries = list(walk(index))
    print(f"asar {len(blob):,} 字节，数据区起点 {data_start:,}，条目 {len(entries)}")

    if list_only:
        for rel, meta in entries:
            size = meta.get("size", 0)
            print(f"{size:>12,}  {rel}")
        return 0

    total = 0
    for rel, meta in entries:
        if meta.get("unpacked"):
            continue
        off = int(meta.get("offset", 0))
        size = meta.get("size", 0)
        dest = os.path.join(out, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(dest) or out, exist_ok=True)
        with open(dest, "wb") as fh:
            fh.write(blob[data_start + off: data_start + off + size])
        total += size

    print(f"已解出 {len(entries)} 个文件，共 {total:,} 字节 → {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
