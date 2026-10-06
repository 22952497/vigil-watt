using Microsoft.Win32;

namespace VigilWatt.App.Services;

/// <summary>开机自启：HKCU Run 注册表键，非打包应用用注册表最省事。</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VigilWatt";

    // 迁移用：旧版注册表值名，**刻意保留旧名字面量**，勿随全局改名替换。
    private const string LegacyValueName = "EasyBluetooth";

    /// <summary>
    /// 把旧版自启项迁到 VigilWatt：以「旧值是否存在」为信号
    /// （AppSettings.Autostart 是死字段，不可依赖）。旧值存在则删除，
    /// 并用当前进程路径重写为 VigilWatt 值。
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) == null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            SetEnabled(true);
        }
        catch
        {
            // 迁移失败不影响启动
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
