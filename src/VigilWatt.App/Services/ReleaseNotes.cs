using System.Globalization;
using VigilWatt.Core.Utils;

namespace VigilWatt.App.Services;

internal enum ReleaseNoteKind
{
    Bullet,
    Subheading,
    Text,
}

internal readonly record struct ReleaseNoteLine(ReleaseNoteKind Kind, string Text);

internal sealed class ReleaseNotesSection
{
    public string Version { get; init; } = "";
    public string? Date { get; init; }
    public IReadOnlyList<ReleaseNoteLine> Lines { get; init; } = Array.Empty<ReleaseNoteLine>();
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// 功 能： 解析随包的 release-notes.md 与更新源给的发行说明，供关于页和更新确认框渲染。
/// 2026/10/18 21:40:00 xliu
/// 格式契约见 docs/project/release-notes.md，打包脚本按同一规则截段，改格式要一起改。
/// </summary>
internal static class ReleaseNotes
{
    private const string FileName = "release-notes.md";

    private static readonly Lazy<IReadOnlyList<ReleaseNotesSection>> Document =
        new(LoadDocument, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>包内全部版本段，顺序同文件（新的在前）。</summary>
    public static IReadOnlyList<ReleaseNotesSection> All => Document.Value;

    /// <summary>按版本号取段；老包或未安装副本里没有这个文件时返回 null。</summary>
    public static ReleaseNotesSection? ForVersion(string? version)
    {
        var want = Normalize(version);
        if (want.Length == 0) return null;
        foreach (var section in All)
        {
            if (string.Equals(Normalize(section.Version), want, StringComparison.OrdinalIgnoreCase))
                return section;
        }

        return null;
    }

    /// <summary>解析更新源里的发行说明：只有一段正文，没有版本标题。空内容返回 null。</summary>
    public static ReleaseNotesSection? FromFeed(string? markdown, string version)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var section = ParseSection(markdown!, version);
        return section.IsEmpty ? null : section;
    }

    private static IReadOnlyList<ReleaseNotesSection> LoadDocument()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                Log.Warn("更新日志文件不存在，关于页将不显示更新内容: " + path);
                return Array.Empty<ReleaseNotesSection>();
            }

            return ParseDocument(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"读取更新日志失败: {ex.Message}");
            return Array.Empty<ReleaseNotesSection>();
        }
    }

    internal static IReadOnlyList<ReleaseNotesSection> ParseDocument(string text)
    {
        var sections = new List<ReleaseNotesSection>();
        string? version = null;
        string? date = null;
        var lines = new List<ReleaseNoteLine>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                version = line[3..].Trim();
                date = null;
                lines = new List<ReleaseNoteLine>();
                continue;
            }

            // 第一个 "## " 之前是维护者说明，不给用户看
            if (version == null) continue;
            AddLine(line, lines, ref date);
        }

        Flush();
        return sections;

        void Flush()
        {
            if (version == null) return;
            sections.Add(new ReleaseNotesSection { Version = version, Date = date, Lines = lines.ToArray() });
        }
    }

    internal static ReleaseNotesSection ParseSection(string text, string version)
    {
        var lines = new List<ReleaseNoteLine>();
        string? date = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // 传进来的是整份文件时，别把版本标题当正文渲染
            if (line.StartsWith("## ", StringComparison.Ordinal)) continue;
            AddLine(line, lines, ref date);
        }

        return new ReleaseNotesSection { Version = version, Date = date, Lines = lines.ToArray() };
    }

    private static void AddLine(string line, List<ReleaseNoteLine> lines, ref string? date)
    {
        var text = line.Trim();
        if (text.Length == 0) return;

        // 日期行只认段首第一行，避免正文里的数字被误吞
        if (date == null && lines.Count == 0 &&
            DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            date = text;
            return;
        }

        if (text.StartsWith("- ", StringComparison.Ordinal))
        {
            lines.Add(new ReleaseNoteLine(ReleaseNoteKind.Bullet, text[2..].Trim()));
            return;
        }

        if (text.StartsWith("### ", StringComparison.Ordinal))
        {
            lines.Add(new ReleaseNoteLine(ReleaseNoteKind.Subheading, text[4..].Trim()));
            return;
        }

        lines.Add(new ReleaseNoteLine(ReleaseNoteKind.Text, text));
    }

    private static string Normalize(string? version) =>
        (version ?? "").Trim().TrimStart('v', 'V');
}
