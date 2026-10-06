using System.Text.Json;
using System.Text.Json.Serialization;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Settings;

public class AppSettings
{
    /// <summary>低于该电量弹提醒，0 关闭。</summary>
    public int LowBatteryThreshold { get; set; } = 20;

    /// <summary>采样间隔（分钟）：无变化时也定时写一条历史。</summary>
    public int SampleIntervalMinutes { get; set; } = 5;

    /// <summary>历史保留天数，过期清理。</summary>
    public int HistoryRetentionDays { get; set; } = 90;

    public bool Autostart { get; set; }

    public bool WidgetEnabled { get; set; }

    public double WidgetX { get; set; } = 60;

    public double WidgetY { get; set; } = 60;

    public bool StartMinimized { get; set; } = true;

    /// <summary>Velopack 更新源：本地文件夹路径、网络共享或 GitHub 仓库 URL；空 = 不检查更新。</summary>
    public string UpdateUrl { get; set; } = "";

    /// <summary>2.4G 插件开关（品牌级）。</summary>
    public Dictionary<string, bool> PluginEnabled { get; set; } = new()
    {
        ["Logitech"] = true,
        ["Mchose"] = false,   // 迈从 dongle 协议未破解，且探测写入可能干扰设备，默认关闭
        ["Rapoo"] = true,     // 雷柏走纯被动心跳读取，零写入，默认开启
        ["Elan"] = true,
        ["Betop"] = true,     // 北通手柄：发官方同款只读状态查询（cmd=0x5），默认开启
    };

    /// <summary>是否允许向迈从 dongle 发送探测/查询写入（有干扰设备的风险，需用户显式开启）。</summary>
    public bool MchoseProbingEnabled { get; set; } = false;

    /// <summary>是否允许向北通手柄接收器发送电量查询。该帧是官方客户端自己使用的只读状态查询
    /// （cmd=0x5，不碰写配置/固件/灯效），风险低，默认开启；关掉即完全静默（不认领、不写入）。</summary>
    public bool BetopProbingEnabled { get; set; } = true;

    [JsonIgnore]
    private static readonly string SettingsPath = Path.Combine(AppPaths.RootDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>进程内缓存：Load 只读一次磁盘（旧版每次 Load 都读文件，插件每轮轮询都会命中）。</summary>
    private static readonly object CacheGate = new();
    private static AppSettings? _cached;

    public static AppSettings Load()
    {
        lock (CacheGate)
        {
            if (_cached != null) return _cached;

            AppSettings? loaded = null;
            try
            {
                if (File.Exists(SettingsPath))
                    loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts);
            }
            catch (Exception ex)
            {
                Log.Warn($"settings.json 读取失败，用默认配置: {ex.Message}");
            }

            _cached = loaded ?? new AppSettings();

            // 老 settings.json 里没有新插件的开关键时补上默认值：反序列化会用 JSON 里的字典
            // **整体替换**默认字典，缺键的插件会被 DeviceManager/设置页的 TryGetValue 判成"关闭"，
            // 导致新增插件（如 Betop）装上去默认不工作、用户还以为是功能没实现。
            // 这里按默认表补齐缺失的键（缺键才补，不覆盖用户已保存的值）。
            var defaults = new AppSettings();
            if (_cached.PluginEnabled == null)
                _cached.PluginEnabled = defaults.PluginEnabled;
            else
                foreach (var (name, on) in defaults.PluginEnabled)
                    _cached.PluginEnabled.TryAdd(name, on);

            return _cached;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
            lock (CacheGate) _cached = this;
        }
        catch (Exception ex)
        {
            Log.Error("settings.json 保存失败", ex);
        }
    }
}
