"""读取 EWEADN 各 vendor 接口的 HID caps（纯读，不开写入）。

用法：python probe-eweadn-caps.py
"""
import ctypes
from ctypes import wintypes

hid = ctypes.WinDLL("hid.dll")
kernel32 = ctypes.WinDLL("kernel32.dll")

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 0x1
FILE_SHARE_WRITE = 0x2
OPEN_EXISTING = 3


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
hid.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]


def caps_of(path: str):
    h = kernel32.CreateFileW(
        path, GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE, None, OPEN_EXISTING, 0, None,
    )
    if h is None or h == wintypes.HANDLE(-1).value:
        # 写入权限被拒时退回只读，仍可读 caps
        h = kernel32.CreateFileW(
            path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, None, OPEN_EXISTING, 0, None,
        )
        if h is None or h == wintypes.HANDLE(-1).value:
            return None, ctypes.get_last_error()
    pp = ctypes.c_void_p()
    if not hid.HidD_GetPreparsedData(h, ctypes.byref(pp)):
        kernel32.CloseHandle(h)
        return None, ctypes.get_last_error()
    caps = HIDP_CAPS()
    ok = hid.HidP_GetCaps(pp, ctypes.byref(caps))
    hid.HidD_FreePreparsedData(pp)
    kernel32.CloseHandle(h)
    if not ok:
        return None, ctypes.get_last_error()
    return caps, 0


PATHS = {
    "KBD MI_03 0xFF60": r"\\?\HID#VID_0C45&PID_FEFE&MI_03#a&b92c947&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
    "KBD MI_04 0xFF60": r"\\?\HID#VID_0C45&PID_FEFE&MI_04#a&79d52f7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
    "KBD MI_02C5 0xFFFF": r"\\?\HID#VID_0C45&PID_FEFE&MI_02&Col05#a&1ec2e585&0&0004#{4d1e55b2-f16f-11cf-88cb-001111000030}",
    "MS  MI_02C1 0xFF00": r"\\?\HID#VID_372E&PID_1026&MI_02&Col01#8&20108b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
    "MS  MI_02C2 0xFF06": r"\\?\HID#VID_372E&PID_1026&MI_02&Col02#8&20108b&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}",
}

for label, path in PATHS.items():
    caps, err = caps_of(path)
    if caps is None:
        print(f"{label:22s} <打开失败 err={err}>")
        continue
    print(
        f"{label:22s} usage=0x{caps.UsagePage:04X}/0x{caps.Usage:04X} "
        f"in={caps.InputReportByteLength} out={caps.OutputReportByteLength} "
        f"feat={caps.FeatureReportByteLength}"
    )
