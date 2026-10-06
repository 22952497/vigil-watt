namespace VigilWatt.Core.Utils;

/// <summary>
/// 统一管理应用数据根目录（%LOCALAPPDATA%\VigilWatt）。
/// </summary>
public static class AppPaths
{
    public const string AppName = "VigilWatt";

    /// <summary>应用数据根目录。</summary>
    public static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
}
