namespace VigilWatt.Core.Models;

/// <summary>设备类型（UI 用来选图标）。**由插件/监视器显式声明，不靠显示名猜**（真机显示名里一个 mouse/keyboard/head 都没有）；分不出来时声明 Unknown，由 <see cref="DeviceKindGuesser"/> 兜底。详见 docs/project/architecture.md §6。</summary>
public enum DeviceKind
{
    Unknown,
    Mouse,
    Keyboard,
    Headset,
    Speaker,
    Gamepad,

    /// <summary>本机（笔记本电池 / 系统电源）。</summary>
    Computer,
}
