"""探针：定向读取指定 VID 设备的 PnP 电量相关属性（只读，SetupAPI）。

比 `Get-PnpDevice | ForEach-Object { Get-PnpDeviceProperty }` 快两个数量级：
后者每个设备都要跨进程调一次 COM，全机几百个节点会卡几分钟。

用法：
    python probe-target-battery.py                 # 默认目标 VID
    python probe-target-battery.py --vid 0C45 372E
    python probe-target-battery.py --all           # 打印匹配节点全部属性
"""
import ctypes
import sys
from ctypes import wintypes as wt

setupapi = ctypes.WinDLL("setupapi", use_last_error=True)

DIGCF_PRESENT = 0x2
DIGCF_ALLCLASSES = 0x4
SPDRP_DEVICEDESC = 0x00
SPDRP_FRIENDLYNAME = 0x0C
DEVPROP_TYPE_BYTE = 0x03
DEVPROP_TYPE_UINT16 = 0x05
DEVPROP_TYPE_UINT32 = 0x07
DEVPROP_TYPE_STRING = 0x12

# 关注的电量/连接相关属性
WATCH_KEYS = {
    "{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2": "BatteryLevel",
    "{104EA319-6EE2-4701-BD47-8DDBF425BBE5},4": "BatteryLevelNormalized",
    "{83DA6326-97A6-4088-9453-A1923F573B29},15": "ContainerId",
    "{A45C254E-DF1C-4EFD-8020-67D146A850E0},2": "DeviceDesc",
    "{A45C254E-DF1C-4EFD-8020-67D146A850E0},24": "BusReportedDeviceDesc",
    "{4340A6C5-93FA-4706-972C-7B648008A5A7},2": "DevNodeStatus",
}

DEFAULT_VIDS = [0x0C45, 0x048D, 0x372E, 0x3302]


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD),
                ("Data4", ctypes.c_ubyte * 8)]


class DEVPROPKEY(ctypes.Structure):
    _fields_ = [("fmtid", GUID), ("pid", wt.ULONG)]


class SP_DEVINFO_DATA(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("ClassGuid", GUID),
                ("DevInst", wt.DWORD), ("Reserved", ctypes.c_void_p)]


def parse_guid(text):
    t = text.strip().strip("{}")
    p = t.split("-")
    g = GUID()
    g.Data1 = int(p[0], 16)
    g.Data2 = int(p[1], 16)
    g.Data3 = int(p[2], 16)
    for i, b in enumerate(bytes.fromhex(p[3] + p[4])):
        g.Data4[i] = b
    return g


def make_key(text):
    guid_text, pid = text.rsplit(",", 1)
    return DEVPROPKEY(fmtid=parse_guid(guid_text), pid=int(pid))


def guid_str(g):
    return (f"{{{g.Data1:08X}-{g.Data2:04X}-{g.Data3:04X}-"
            f"{bytes(g.Data4[:2]).hex().upper()}-"
            f"{bytes(g.Data4[2:]).hex().upper()}}}")


setupapi.SetupDiGetClassDevsW.restype = wt.HANDLE
setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(GUID), wt.LPCWSTR, wt.HWND, wt.DWORD]
setupapi.SetupDiEnumDeviceInfo.restype = wt.BOOL
setupapi.SetupDiEnumDeviceInfo.argtypes = [wt.HANDLE, wt.DWORD, ctypes.POINTER(SP_DEVINFO_DATA)]
setupapi.SetupDiGetDeviceInstanceIdW.restype = wt.BOOL
setupapi.SetupDiGetDeviceInstanceIdW.argtypes = [wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA),
                                                 wt.LPWSTR, wt.DWORD, ctypes.POINTER(wt.DWORD)]
setupapi.SetupDiGetDeviceRegistryPropertyW.restype = wt.BOOL
setupapi.SetupDiGetDeviceRegistryPropertyW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), wt.DWORD, ctypes.POINTER(wt.DWORD),
    ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD)]
setupapi.SetupDiGetDevicePropertyW.restype = wt.BOOL
setupapi.SetupDiGetDevicePropertyW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), ctypes.POINTER(DEVPROPKEY),
    ctypes.POINTER(wt.ULONG), ctypes.c_void_p, wt.DWORD,
    ctypes.POINTER(wt.DWORD), wt.DWORD]
setupapi.SetupDiGetDevicePropertyKeys.restype = wt.BOOL
setupapi.SetupDiGetDevicePropertyKeys.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), ctypes.c_void_p, wt.DWORD,
    ctypes.POINTER(wt.DWORD), wt.DWORD]
setupapi.SetupDiDestroyDeviceInfoList.restype = wt.BOOL
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wt.HANDLE]


def read_property(hdev, did, key):
    ptype = wt.ULONG()
    size = wt.DWORD(0)
    setupapi.SetupDiGetDevicePropertyW(hdev, ctypes.byref(did), ctypes.byref(key),
                                       ctypes.byref(ptype), None, 0, ctypes.byref(size), 0)
    if size.value == 0:
        return None
    buf = ctypes.create_string_buffer(size.value)
    if not setupapi.SetupDiGetDevicePropertyW(hdev, ctypes.byref(did), ctypes.byref(key),
                                              ctypes.byref(ptype), buf, size.value,
                                              ctypes.byref(size), 0):
        return None
    return ptype.value, buf.raw[: size.value]


def decode(ptype, raw):
    if ptype == DEVPROP_TYPE_BYTE:
        return raw[0]
    if ptype == DEVPROP_TYPE_UINT16:
        return int.from_bytes(raw[:2], "little")
    if ptype == DEVPROP_TYPE_UINT32:
        return int.from_bytes(raw[:4], "little")
    if ptype == DEVPROP_TYPE_STRING:
        return raw.decode("utf-16-le", errors="replace").rstrip("\x00")
    return raw.hex()


def dump_all(hdev, did):
    count = wt.DWORD(0)
    setupapi.SetupDiGetDevicePropertyKeys(hdev, ctypes.byref(did), None, 0,
                                          ctypes.byref(count), 0)
    if count.value == 0:
        return []
    arr = (DEVPROPKEY * count.value)()
    if not setupapi.SetupDiGetDevicePropertyKeys(hdev, ctypes.byref(did), ctypes.byref(arr),
                                                 count.value, ctypes.byref(count), 0):
        return []
    out = []
    for k in arr:
        got = read_property(hdev, did, k)
        name = f"{guid_str(k.fmtid)},{k.pid}"
        out.append((name, "(unreadable)" if got is None else decode(*got)))
    return out


def main():
    argv = sys.argv[1:]
    vids = []
    show_all = "--all" in argv
    i = 0
    while i < len(argv):
        if argv[i] == "--vid" and i + 1 < len(argv):
            vids.append(int(argv[i + 1], 16))
            i += 2
            continue
        i += 1
    vids = vids or DEFAULT_VIDS
    wanted = [f"VID_{v:04X}" for v in vids]

    keys = [(make_key(k), label) for k, label in WATCH_KEYS.items()]

    hdev = setupapi.SetupDiGetClassDevsW(None, None, None, DIGCF_PRESENT | DIGCF_ALLCLASSES)
    if not hdev or hdev == wt.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())

    hits = []
    try:
        did = SP_DEVINFO_DATA()
        did.cbSize = ctypes.sizeof(SP_DEVINFO_DATA)
        idx = 0
        while setupapi.SetupDiEnumDeviceInfo(hdev, idx, ctypes.byref(did)):
            idx += 1
            buf = ctypes.create_unicode_buffer(1024)
            if not setupapi.SetupDiGetDeviceInstanceIdW(hdev, ctypes.byref(did), buf,
                                                        len(buf), None):
                continue
            inst = buf.value
            if not any(w in inst.upper() for w in wanted):
                continue

            nb = ctypes.create_unicode_buffer(512)
            if not setupapi.SetupDiGetDeviceRegistryPropertyW(hdev, ctypes.byref(did),
                                                              SPDRP_FRIENDLYNAME, None,
                                                              nb, ctypes.sizeof(nb), None):
                setupapi.SetupDiGetDeviceRegistryPropertyW(hdev, ctypes.byref(did),
                                                           SPDRP_DEVICEDESC, None,
                                                           nb, ctypes.sizeof(nb), None)
            name = nb.value

            vals = {}
            for key, label in keys:
                got = read_property(hdev, did, key)
                if got is not None:
                    vals[label] = decode(*got)
            hits.append((inst, name, vals, (hdev, did) if show_all else None))

        for inst, name, vals, _ in hits:
            print(f"--- {name}")
            print(f"    {inst}")
            for k, v in vals.items():
                print(f"      {k} = {v}")

        if show_all:
            print("\n\n########## 全属性 ##########")
            for inst, name, _, _ in hits:
                print(f"\n--- {name}\n    {inst}")
                for k, v in dump_all(hdev, did):
                    print(f"      {k} = {v}")
    finally:
        setupapi.SetupDiDestroyDeviceInfoList(hdev)


if __name__ == "__main__":
    main()
