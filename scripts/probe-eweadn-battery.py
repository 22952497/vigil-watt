"""EWEADN X87 键盘 2.4G 电量读取——官方协议只读验证。

帧来源：官方 DeviceDriver.exe（sha256 b242aa92…）FUN_004364e0 + FUN_00450960 反编译，
       config.xml 指定 MI_03 为 2.4G 通道。仅发送官方同一帧，不枚举、不扫描、不改字节。

用法：python probe-eweadn-battery.py [次数]
"""
import ctypes
import sys
import time
from ctypes import wintypes

kernel32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)
hid = ctypes.WinDLL("hid.dll")

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 0x1
FILE_SHARE_WRITE = 0x2
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000   # 官方 FUN_00452590/452670 全走异步 I/O
INFINITE_WAIT = 0xFFFFFFFF
WAIT_OBJECT_0 = 0


class OVERLAPPED(ctypes.Structure):
    _fields_ = [
        ("Internal", ctypes.c_void_p),
        ("InternalHigh", ctypes.c_void_p),
        ("Offset", wintypes.DWORD),
        ("OffsetHigh", wintypes.DWORD),
        ("hEvent", wintypes.HANDLE),
    ]

# config.xml: 2.4G TYPE-A → VID_0C45&PID_FEFE&MI_03
KBD_PATH = (
    r"\\?\HID#VID_0C45&PID_FEFE&MI_03#a&b92c947&0&0000"
    r"#{4d1e55b2-f16f-11cf-88cb-001111000030}"
)

REQ_LEN = 0x41   # 官方 memset 0x41，校验和写在 [32]
TX_LEN = 0x21    # 官方 FUN_00452590 实发 33 字节


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [
        ("Usage", wintypes.USHORT),
        ("UsagePage", wintypes.USHORT),
        ("InputReportByteLength", wintypes.USHORT),
        ("OutputReportByteLength", wintypes.USHORT),
        ("FeatureReportByteLength", wintypes.USHORT),
        ("Reserved", wintypes.USHORT * 17),
        ("NumberLinkCollectionNodes", wintypes.USHORT),
        ("NumberInputButtonCaps", wintypes.USHORT),
        ("NumberInputValueCaps", wintypes.USHORT),
        ("NumberInputDataIndices", wintypes.USHORT),
        ("NumberOutputButtonCaps", wintypes.USHORT),
        ("NumberOutputValueCaps", wintypes.USHORT),
        ("NumberOutputDataIndices", wintypes.USHORT),
        ("NumberFeatureButtonCaps", wintypes.USHORT),
        ("NumberFeatureValueCaps", wintypes.USHORT),
        ("NumberFeatureDataIndices", wintypes.USHORT),
    ]


kernel32.CreateFileW.restype = wintypes.HANDLE
kernel32.CreateFileW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
    wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE,
]
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.WriteFile.argtypes = [
    wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p,
]
kernel32.ReadFile.argtypes = [
    wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p,
]
kernel32.CreateEventW.restype = wintypes.HANDLE
kernel32.CreateEventW.argtypes = [
    ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR,
]
kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel32.GetOverlappedResult.argtypes = [
    wintypes.HANDLE, ctypes.POINTER(OVERLAPPED),
    ctypes.POINTER(wintypes.DWORD), wintypes.BOOL,
]
hid.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]


def build_request() -> bytes:
    """官方 FUN_004364e0 构造：65 字节全零，[1]=0x20，[2]=0x01，[32]=前 65 字节和。"""
    buf = bytearray(REQ_LEN)
    buf[1] = 0x20
    buf[2] = 0x01
    buf[32] = sum(buf) & 0xFF
    return bytes(buf)


def open_device(path: str):
    h = kernel32.CreateFileW(
        path, GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE, None, OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED, None,
    )
    if h == wintypes.HANDLE(-1).value or h is None:
        raise OSError(ctypes.get_last_error(), "CreateFileW 失败")
    return h


def caps_of(handle):
    pp = ctypes.c_void_p()
    if not hid.HidD_GetPreparsedData(handle, ctypes.byref(pp)):
        raise OSError(ctypes.get_last_error(), "HidD_GetPreparsedData 失败")
    caps = HIDP_CAPS()
    ok = hid.HidP_GetCaps(pp, ctypes.byref(caps))
    hid.HidD_FreePreparsedData(pp)
    if not ok:
        raise OSError(ctypes.get_last_error(), "HidP_GetCaps 失败")
    return caps


def query_battery(handle, event) -> int | None:
    """发 1 帧官方电量查询，读响应，返回电量百分比。"""
    req = build_request()
    tx = req[:TX_LEN]

    buf = ctypes.create_string_buffer(tx, len(tx))
    written = wintypes.DWORD(0)
    ov = OVERLAPPED()
    ov.hEvent = event
    kernel32.ResetEvent(event)
    ok = kernel32.WriteFile(handle, buf, len(tx), ctypes.byref(written), ctypes.byref(ov))
    if not ok and ctypes.get_last_error() != 0x3E5:
        print(f"  WriteFile 失败 err={ctypes.get_last_error()}")
        return None
    if kernel32.WaitForSingleObject(event, 1000) != WAIT_OBJECT_0:
        print("  WriteFile 超时")
        return None
    print(f"  tx({len(tx)}B): {' '.join(f'{b:02X}' for b in tx)}")

    # 官方 FUN_00452670：首字节 0x00 = 有 report ID，回读时剥掉
    for _ in range(20):
        time.sleep(0.01)
        rbuf = ctypes.create_string_buffer(64)
        got = wintypes.DWORD(0)
        rov = OVERLAPPED()
        rov.hEvent = event
        kernel32.ResetEvent(event)
        ok = kernel32.ReadFile(handle, rbuf, 64, ctypes.byref(got), ctypes.byref(rov))
        if not ok and ctypes.get_last_error() != 0x3E5:
            print(f"  ReadFile 失败 err={ctypes.get_last_error()}")
            return None
        # 官方单次等待 5ms，超时就再来一轮，共 20 轮
        if kernel32.WaitForSingleObject(event, 5) != WAIT_OBJECT_0:
            kernel32.CancelIo(handle)
            continue
        if not kernel32.GetOverlappedResult(
            handle, ctypes.byref(rov), ctypes.byref(got), False
        ):
            continue
        if got.value == 0:
            continue
        raw = rbuf.raw[: got.value]
        print(f"  rx({got.value}B): {' '.join(f'{b:02X}' for b in raw[:12])} …")
        body = raw[1:] if raw[0] == 0x00 else raw
        # 官方判据：响应 [0]==0x20 且 [1]==0x01 且 [2]==0x00，电量在 [3]
        if len(body) >= 4 and body[0] == 0x20 and body[1] == 0x01 and body[2] == 0x00:
            return min(body[3], 100)
        print("  （回声不匹配，继续等）")
    return None


def main():
    times = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    print(f"目标：{KBD_PATH}")
    h = open_device(KBD_PATH)
    event = kernel32.CreateEventW(None, True, False, None)
    try:
        caps = caps_of(h)
        print(
            f"caps: usage=0x{caps.UsagePage:04X}/0x{caps.Usage:04X} "
            f"in={caps.InputReportByteLength} out={caps.OutputReportByteLength}"
        )
        for i in range(times):
            print(f"\n[{i + 1}/{times}] 发送官方电量查询帧")
            pct = query_battery(h, event)
            if pct is None:
                print("  未取到电量")
            else:
                print(f"  ==> 电量 {pct}%")
            if i + 1 < times:
                time.sleep(1.0)
    finally:
        kernel32.CloseHandle(h)


if __name__ == "__main__":
    main()
