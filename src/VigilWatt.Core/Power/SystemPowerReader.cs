using System.Runtime.InteropServices;

namespace VigilWatt.Core.Power;

/// <summary>本机电源状态的一次快照。</summary>
/// <param name="HasBattery">本机是否装有可用电池（台式机 / 电池被禁用时为 false）。</param>
/// <param name="Percent">0-100；无电池或系统报告未知时为 null。</param>
/// <param name="AcOnline">是否接着交流电（true 也可能同时在放电，如"接电但未充电"）。</param>
public readonly record struct SystemPowerSnapshot(bool HasBattery, int? Percent, bool AcOnline)
{
    /// <summary>什么都没有的快照（读取失败时用）。</summary>
    public static SystemPowerSnapshot Unavailable => new(false, null, false);
}

/// <summary>
/// 读本机电源状态的纯 P/Invoke 实现，底层是 <c>GetSystemPowerStatus</c>。
///
/// 为什么用这个 API 而不是 WinRT 的 <c>Battery.AggregateBattery</c>：
/// 项目里 WinRT 只用于 BLE（见 architecture.md 的"Core 无 UI 依赖"例外条款），
/// 而系统电量是个纯读数、不需要事件源，一个 kernel32 调用就够，
/// 也不必为它引入 WinRT 投影和 <c>Battery</c> 的异步/COM 生命周期。
///
/// <see cref="SYSTEM_POWER_STATUS.BatteryFlag"/> 的 <c>128</c> 位是"无系统电池"，
/// <c>255</c> 是"状态未知"，两者都不能当成 0% 电池——这正是台式机场景的判定依据。
/// </summary>
public static class SystemPowerReader
{
    /// <summary>BatteryFlag 位：无系统电池。</summary>
    private const byte NoSystemBatteryFlag = 128;

    /// <summary>BatteryFlag 位：状态未知（读不到电池信息）。</summary>
    private const byte UnknownStatusFlag = 255;

    /// <summary>BatteryLifePercent 的未知哨兵值。</summary>
    private const byte UnknownPercent = 255;

    /// <summary>ACLineStatus：0 = 离线（电池供电），1 = 在线（交流电），255 = 未知。</summary>
    private const byte AcOnlineValue = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>
    /// 读一次电源状态。任何失败都退化为 <see cref="SystemPowerSnapshot.Unavailable"/>，
    /// 绝不抛异常——它是每分钟轮询路径上的一环，不该因为读不到电量而打断整轮。
    /// </summary>
    public static SystemPowerSnapshot Read()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return SystemPowerSnapshot.Unavailable;
            return Interpret(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent);
        }
        catch
        {
            return SystemPowerSnapshot.Unavailable;
        }
    }

    /// <summary>
    /// 把三个原始字节翻译成快照。**独立出来是为了可单测**——
    /// 真机上没法伪造"台式机无电池"或"接电但 255%"这些场景。
    /// </summary>
    internal static SystemPowerSnapshot Interpret(byte acLineStatus, byte batteryFlag, byte batteryLifePercent)
    {
        bool acOnline = acLineStatus == AcOnlineValue;
        bool noBattery = batteryFlag == NoSystemBatteryFlag || batteryFlag == UnknownStatusFlag;
        if (noBattery) return new SystemPowerSnapshot(false, null, acOnline);

        int? percent = batteryLifePercent == UnknownPercent ? null : batteryLifePercent;

        // 有电池但百分比未知：仍然算"有电池"，让界面能显示"电源："这类降级文案，
        // 而不是整行消失（有电池的笔记本不该因为一次读不到百分比就假装自己没有电池）。
        return new SystemPowerSnapshot(true, percent is >= 0 and <= 100 ? percent : null, acOnline);
    }
}
