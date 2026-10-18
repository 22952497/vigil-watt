# -*- coding: utf-8 -*-
"""
HID 接口能力探针（纯 ctypes，只读）——回答"这个接口到底声明了哪些报告"。
用途：判断某设备是否存在主机→设备通道（OUT/Feature 长度是否为 0）、
以及除了主 TLC 之外是否还藏着厂商页集合（逐条列 button caps 的 UsagePage）。

用法：python probe-hid-caps.py [VID:PID 前缀，如 38FF]
只调用 HidD_GetPreparsedData / HidP_GetCaps / HidP_GetButtonCaps，不写任何报告。
"""
import ctypes
from ctypes import wintypes
import sys

hid = ctypes.WinDLL('hid.dll')
setupapi = ctypes.WinDLL('setupapi.dll')
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 0x1
FILE_SHARE_WRITE = 0x2
OPEN_EXISTING = 3
DIGCF_PRESENT = 0x2
DIGCF_DEVICEINTERFACE = 0x10
ERROR_INSUFFICIENT_BUFFER = 122
ERROR_NO_MORE_ITEMS = 259


class GUID(ctypes.Structure):
    _fields_ = [('Data1', wintypes.DWORD), ('Data2', wintypes.WORD),
                ('Data3', wintypes.WORD), ('Data4', ctypes.c_ubyte * 8)]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [('cbSize', wintypes.DWORD), ('InterfaceClassGuid', GUID),
                ('Flags', wintypes.DWORD), ('Reserved', ctypes.POINTER(wintypes.ULONG))]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [('Usage', wintypes.USHORT), ('UsagePage', wintypes.USHORT),
                ('InputReportByteLength', wintypes.USHORT),
                ('OutputReportByteLength', wintypes.USHORT),
                ('FeatureReportByteLength', wintypes.USHORT),
                ('Reserved', wintypes.USHORT * 17),
                ('NumberLinkCollectionNodes', wintypes.USHORT),
                ('NumberInputButtonCaps', wintypes.USHORT),
                ('NumberInputValueCaps', wintypes.USHORT),
                ('NumberInputDataIndices', wintypes.USHORT),
                ('NumberOutputButtonCaps', wintypes.USHORT),
                ('NumberOutputValueCaps', wintypes.USHORT),
                ('NumberOutputDataIndices', wintypes.USHORT),
                ('NumberFeatureButtonCaps', wintypes.USHORT),
                ('NumberFeatureValueCaps', wintypes.USHORT),
                ('NumberFeatureDataIndices', wintypes.USHORT)]


BUTTON_CAP_SIZE = 72  # sizeof(HIDP_BUTTON_CAPS)：16 字节固定头 + 40 字节 Reserved + 16 字节 union


setupapi.SetupDiGetClassDevsW.restype = ctypes.c_void_p
setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p,
                                          ctypes.c_void_p, wintypes.DWORD]
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                                 ctypes.c_void_p, wintypes.DWORD,
                                                 ctypes.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [ctypes.c_void_p]
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                                      ctypes.c_void_p, wintypes.DWORD,
                                                      ctypes.c_void_p, ctypes.c_void_p]
hid.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
hid.HidP_GetButtonCaps.argtypes = [ctypes.c_int, ctypes.c_void_p,
                                   ctypes.POINTER(wintypes.USHORT), ctypes.c_void_p]
kernel32.CreateFileW.restype = wintypes.HANDLE
kernel32.CreateFileW.argtypes = [ctypes.c_wchar_p, wintypes.DWORD, wintypes.DWORD,
                                 ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD,
                                 ctypes.c_void_p]


def hid_interface_paths():
    guid = GUID(0x4D1E55B2, 0xF16F, 0x11CF,
                (ctypes.c_ubyte * 8)(0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30))
    hdev = setupapi.SetupDiGetClassDevsW(ctypes.byref(guid), None, None,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    if not hdev or hdev == 0xFFFFFFFFFFFFFFFF:
        raise ctypes.WinError(ctypes.get_last_error())
    did = SP_DEVICE_INTERFACE_DATA()
    did.cbSize = ctypes.sizeof(SP_DEVICE_INTERFACE_DATA)
    out = []
    i = 0
    while setupapi.SetupDiEnumDeviceInterfaces(hdev, None, ctypes.byref(guid), i, ctypes.byref(did)):
        need = wintypes.DWORD(0)
        setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(did), None, 0,
                                                  ctypes.byref(need), None)
        buf = ctypes.create_string_buffer(need.value)
        ctypes.cast(buf, ctypes.POINTER(wintypes.DWORD))[0] = ctypes.sizeof(wintypes.DWORD) * 2
        if setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(did), buf, need.value,
                                                     ctypes.byref(need), None):
            out.append(ctypes.wstring_at(ctypes.addressof(buf) + 4))
        i += 1
    setupapi.SetupDiDestroyDeviceInfoList(hdev)
    return out


def dump_caps(path):
    h = kernel32.CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                             FILE_SHARE_READ | FILE_SHARE_WRITE, None,
                             OPEN_EXISTING, 0, None)
    if h == wintypes.HANDLE(-1).value:
        print(f"  打开失败 (err={ctypes.get_last_error()})")
        return
    preparsed = ctypes.c_void_p()
    if not hid.HidD_GetPreparsedData(h, ctypes.byref(preparsed)):
        print("  HidD_GetPreparsedData 失败")
        kernel32.CloseHandle(h)
        return
    caps = HIDP_CAPS()
    if not hid.HidP_GetCaps(preparsed, ctypes.byref(caps)):
        print("  HidP_GetCaps 失败")
    else:
        print(f"  UsagePage=0x{caps.UsagePage:04X} Usage=0x{caps.Usage:04X}")
        print(f"  报告长度: IN={caps.InputReportByteLength}B "
              f"OUT={caps.OutputReportByteLength}B FEATURE={caps.FeatureReportByteLength}B")
        print(f"  ButtonCaps: IN={caps.NumberInputButtonCaps} "
              f"OUT={caps.NumberOutputButtonCaps} FEAT={caps.NumberFeatureButtonCaps}   "
              f"ValueCaps: IN={caps.NumberInputValueCaps} "
              f"OUT={caps.NumberOutputValueCaps} FEAT={caps.NumberFeatureValueCaps}")
        n = caps.NumberInputButtonCaps
        if n:
            buf = ctypes.create_string_buffer(BUTTON_CAP_SIZE * n)
            ln = wintypes.USHORT(n)
            if hid.HidP_GetButtonCaps(0, buf, ctypes.byref(ln), preparsed):  # 0 = HidP_Input
                for k in range(ln.value):
                    e = buf.raw[k * BUTTON_CAP_SIZE:(k + 1) * BUTTON_CAP_SIZE]
                    up = int.from_bytes(e[0:2], 'little')
                    rid = e[2]
                    is_range = e[12]
                    if is_range:
                        umin = int.from_bytes(e[56:58], 'little')
                        umax = int.from_bytes(e[58:60], 'little')
                        desc = f"usage 0x{umin:04X}-0x{umax:04X}"
                    else:
                        u = int.from_bytes(e[56:58], 'little')
                        desc = f"usage 0x{u:04X}"
                    print(f"    [in] page=0x{up:04X} reportID=0x{rid:02X} {desc}")
    hid.HidD_FreePreparsedData(preparsed)
    kernel32.CloseHandle(h)


def main():
    needle = (sys.argv[1] if len(sys.argv) > 1 else '').upper()
    paths = [p for p in hid_interface_paths() if needle in p.upper()]
    if not paths:
        print("没有匹配的 HID 接口（设备未接入？）")
        return
    for p in paths:
        print(p)
        dump_caps(p)
        print()


main()
