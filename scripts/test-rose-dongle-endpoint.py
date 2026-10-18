# -*- coding: utf-8 -*-
"""
弱水时砂 dongle（38FF:0001）接口 3 端点只读探针——绕过 HID 栈直读物理端点。

前提：MI_03 已用 Zadig 换成 WinUSB/libusbK。HID 驱动下 libusb 打不开设备，
      而 HID 栈只按描述符声明的 3 字节取包，端点上的长帧会被它丢掉。

只读：不发送任何控制传输、不写任何输出报告，只 claim 接口后从 IN 端点读。
用法：
  python test-rose-dongle-endpoint.py --info            # 只打印配置/接口/端点
  python test-rose-dongle-endpoint.py --seconds 300     # 监听 5 分钟
  python test-rose-dongle-endpoint.py --ep 0x82 --seconds 60
"""
import argparse
import sys
import time

import usb.core
import usb.util

try:
    import libusb_package
    BACKEND = libusb_package.get_libusb1_backend()
except Exception:  # 没有 libusb-package 时退回系统 DLL
    BACKEND = None

VID, PID = 0x38FF, 0x0001


def open_device():
    dev = usb.core.find(idVendor=VID, idProduct=PID, backend=BACKEND)
    if dev is None:
        print("设备不在位（或未绑定 WinUSB）")
        sys.exit(1)
    return dev


def dump_info(dev):
    print(f"设备: idVendor=0x{dev.idVendor:04X} idProduct=0x{dev.idProduct:04X} "
          f"bcdUSB=0x{dev.bcdUSB:04X} bcdDevice=0x{dev.bcdDevice:04X}")
    try:
        cfg = dev.get_active_configuration()
    except usb.core.USBError as ex:
        print(f"读取当前配置失败: {ex}")
        return None
    print(f"配置: bConfigurationValue={cfg.bConfigurationValue} "
          f"bNumInterfaces={cfg.bNumInterfaces} bmAttributes=0x{cfg.bmAttributes:02X}")
    for intf in cfg:
        print(f"  接口 {intf.bInterfaceNumber} alt={intf.bAlternateSetting} "
              f"class=0x{intf.bInterfaceClass:02X}/0x{intf.bInterfaceSubClass:02X}/"
              f"0x{intf.bInterfaceProtocol:02X} 端点 {intf.bNumEndpoints}")
        for ep in intf:
            direction = 'IN' if ep.bEndpointAddress & 0x80 else 'OUT'
            print(f"    0x{ep.bEndpointAddress:02X} {direction} "
                  f"attr=0x{ep.bmAttributes:02X} wMaxPacketSize={ep.wMaxPacketSize} "
                  f"bInterval={ep.bInterval}")
    return cfg


def read_endpoint(dev, address, seconds, max_len):
    try:
        usb.util.claim_interface(dev, 3)
    except usb.core.USBError as ex:
        print(f"claim 接口 3 失败: {ex}（是否已被其他驱动占用？）")
        return
    print(f"只读监听 0x{address:02X} {seconds:.0f}s —— 期间可戴耳机、按键、播放/暂停")
    t0 = time.time()
    n = 0
    sizes = {}
    try:
        while time.time() - t0 < seconds:
            try:
                data = dev.read(address, max_len, timeout=300)
            except usb.core.USBTimeoutError:
                continue
            except usb.core.USBError as ex:
                print(f"[{time.strftime('%H:%M:%S')}] 读错误: {ex}")
                break
            if not data:
                continue
            n += 1
            sizes[len(data)] = sizes.get(len(data), 0) + 1
            print(f"[{time.strftime('%H:%M:%S')}] ({len(data)}B) "
                  f"{' '.join(f'{b:02X}' for b in data)}", flush=True)
    finally:
        usb.util.release_interface(dev, 3)
    print(f"共 {n} 帧；长度分布 {sizes if sizes else '无'}")
    if n == 0:
        print("提示：全程零帧不代表端点死了——设备可能只在特定事件（连上耳机/低电量）才推。")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--info', action='store_true')
    ap.add_argument('--seconds', type=float, default=300)
    ap.add_argument('--ep', type=lambda s: int(s, 0), default=0x82)
    ap.add_argument('--len', type=int, default=64)
    args = ap.parse_args()

    dev = open_device()
    if args.info:
        dump_info(dev)
        return
    if dump_info(dev) is None:
        return
    read_endpoint(dev, args.ep, args.seconds, args.len)


if __name__ == '__main__':
    main()
