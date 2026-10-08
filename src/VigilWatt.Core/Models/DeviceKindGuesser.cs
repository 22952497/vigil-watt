namespace VigilWatt.Core.Models;

/// <summary>
/// 插件没有声明设备类型（<c>DeviceStatus.Kind</c>）时，按显示名猜（纯逻辑，可单测）。
/// 主要给"一个接收器管多种设备"的插件兜底（如罗技），其余插件应直接声明 Kind。
/// </summary>
public static class DeviceKindGuesser
{
    /// <summary>
    /// 按显示名猜类型。
    ///
    /// ⚠️ **手柄必须排在键盘/鼠标之前**：北通的 USB 产品串是
    /// <c>"BEITONG T9 For Keyboard & Mouse"</c>，先匹配 keyboard/mouse 会把手柄认成键盘
    /// （真机踩过）。同一条规则在 UI 侧也用过，所以抽到这里统一。
    /// </summary>
    public static DeviceKind Guess(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return DeviceKind.Unknown;
        var n = displayName.ToLowerInvariant();

        if (Contains(n, "手柄", "gamepad", "controller", "joystick",
                        "北通", "betop", "beitong", "xbox"))
            return DeviceKind.Gamepad;
        // 罗技 MX Master / Anywhere 等产品名不含 "mouse"（真机 "MX Master 3S"）
        if (Contains(n, "鼠标", "mouse",
                        "mx master", "mx anywhere", "mx ergo", "mx vertical",
                        "g502", "g304", "g305", "g pro", "superlight"))
            return DeviceKind.Mouse;
        if (Contains(n, "键盘", "keyboard")) return DeviceKind.Keyboard;
        if (Contains(n, "音箱", "speaker")) return DeviceKind.Speaker;
        if (Contains(n, "耳机", "head", "airpods", "buds")) return DeviceKind.Headset;

        return DeviceKind.Unknown;
    }

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (var x in needles)
            if (haystack.Contains(x, StringComparison.Ordinal)) return true;
        return false;
    }
}
