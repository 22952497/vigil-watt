namespace EasyBluetooth.Core.Models;

/// <summary>
/// 设备类型（UI 用来选图标）。**由插件/监视器显式声明，不靠显示名猜**——
/// 真机踩过：雷柏鼠标叫 "Rapoo Gaming Device"、迈从键盘叫 "MCHOSE 2.4G Wireless"、
/// 耳机叫 "EDIFIER W820NB 双金标版"，名字里一个 "mouse/keyboard/head" 都没有，
/// 按名字猜的结果是全部退化成通用蓝牙图标。
/// 插件确实分不出来时（如罗技接收器同时管鼠标和键盘）声明 Unknown，
/// 由 <see cref="DeviceKindGuesser"/> 按名字兜底。
/// </summary>
public enum DeviceKind
{
    Unknown,
    Mouse,
    Keyboard,
    Headset,
    Speaker,
    Gamepad,
}
