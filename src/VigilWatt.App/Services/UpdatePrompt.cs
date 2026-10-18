using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Velopack;
using VigilWatt.Core.Utils;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 发现新版本时的确认框，启动检查与关于页手动检查共用。
/// 2026/10/18 21:47:00 xliu
/// 更新内容取自更新源的 NotesMarkdown；取不到就只显示版本行，不要空框。
/// </summary>
internal static class UpdatePrompt
{
    /// <summary>true = 用户选了「下载并安装」。closeText 由调用方决定（稍后 / 取消）。</summary>
    public static async Task<bool> ShowAsync(UpdateService service, UpdateInfo info, XamlRoot root, string closeText)
    {
        var version = info.TargetFullRelease.Version.ToString();
        var muted = ReleaseNotesView.Brush("TextFillColorSecondaryBrush");

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"当前 {service.CurrentVersion} → 最新 {version}",
            FontSize = 13,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
        });

        var notes = ReleaseNotes.FromFeed(info.TargetFullRelease.NotesMarkdown, version);
        if (notes != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "更新内容",
                FontSize = 12,
                Foreground = muted,
                Margin = new Thickness(0, 4, 0, 0),
            });
            panel.Children.Add(ReleaseNotesView.BuildScrollable(notes, 320)!);
        }
        else
        {
            Log.Info($"版本 {version} 没有可显示的更新说明（更新源未提供 notes）");
        }

        panel.Children.Add(new TextBlock
        {
            Text = "安装完成后应用会自动重启",
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = "发现新版本 " + version,
            Content = panel,
            PrimaryButtonText = "下载并安装",
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
