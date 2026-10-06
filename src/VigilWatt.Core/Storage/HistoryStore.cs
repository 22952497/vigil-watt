using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;
using Microsoft.Data.Sqlite;

namespace VigilWatt.Core.Storage;

public record HistorySample(long Id, string DeviceId, string Name, int Percent, bool Charging, DateTimeOffset Timestamp);

/// <summary>电量历史 SQLite 存储。库文件在 %LOCALAPPDATA%\VigilWatt\history.db。</summary>
public class HistoryStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _writeGate = new();

    public HistoryStore()
    {
        var dir = AppPaths.RootDir;
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "history.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        EnsureSchema();
        Log.Info($"history.db 就绪: {dbPath}");
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id TEXT NOT NULL,
                name TEXT NOT NULL,
                percent INTEGER NOT NULL,
                charging INTEGER NOT NULL,
                ts INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_samples_device_ts ON samples(device_id, ts);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Insert(DeviceStatus device)
    {
        if (device.BatteryPercent is not { } percent) return;
        lock (_writeGate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO samples(device_id, name, percent, charging, ts) VALUES($id, $name, $p, $c, $ts)";
            cmd.Parameters.AddWithValue("$id", device.Id);
            cmd.Parameters.AddWithValue("$name", device.DisplayName);
            cmd.Parameters.AddWithValue("$p", percent);
            cmd.Parameters.AddWithValue("$c", device.Charging == ChargingState.Charging ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", device.LastUpdated.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>取时间范围内的采样，用于画曲线。</summary>
    public List<HistorySample> Query(string deviceId, DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<HistorySample>();
        lock (_writeGate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT id, device_id, name, percent, charging, ts FROM samples " +
                "WHERE device_id = $id AND ts BETWEEN $from AND $to ORDER BY ts";
            cmd.Parameters.AddWithValue("$id", deviceId);
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new HistorySample(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4) == 1,
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
            }
        }
        return list;
    }

    /// <summary>时间范围内有过采样的设备（历史页设备列表用：按库里的数据走，而不是当前在线设备）。
    /// 排序 = 最近有数据的在前。</summary>
    public List<(string DeviceId, string Name, DateTimeOffset LastSeen)> DistinctDevices(DateTimeOffset since)
    {
        var list = new List<(string, string, DateTimeOffset)>();
        lock (_writeGate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT device_id, MAX(name), MAX(ts) FROM samples WHERE ts >= $since GROUP BY device_id ORDER BY MAX(ts) DESC";
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetString(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        }
        return list;
    }

    public int CleanupOlderThan(DateTimeOffset cutoff)
    {
        lock (_writeGate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
