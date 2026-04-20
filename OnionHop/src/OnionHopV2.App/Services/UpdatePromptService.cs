using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using OnionHopV2.Core.Models;
using SukiUI.Controls;

namespace OnionHopV2.App.Services;

internal static class UpdatePromptService
{
    public static async Task ShowUpdateAvailableAsync(string currentVersion, UpdateInfo updateInfo, string fallbackReleaseUrl)
    {
        var releaseUrl = !string.IsNullOrWhiteSpace(updateInfo.HtmlUrl)
            ? updateInfo.HtmlUrl!
            : fallbackReleaseUrl;

        var title = LocalizationService.Get("Update.AvailableTitle");
        var body = LocalizationService.Get("Update.AvailableBody");
        var currentText = string.Format(CultureInfo.CurrentCulture, LocalizationService.Get("Update.CurrentVersion"), currentVersion);
        var latestText = string.Format(CultureInfo.CurrentCulture, LocalizationService.Get("Update.LatestVersion"), updateInfo.Version);

        var openButton = new Button
        {
            Content = LocalizationService.Get("Update.OpenReleasePage"),
            MinWidth = 160,
            Height = 38
        };

        var laterButton = new Button
        {
            Content = LocalizationService.Get("Update.Later"),
            MinWidth = 120,
            Height = 38
        };
        laterButton.Classes.Add("Flat");

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttonRow.Children.Add(laterButton);
        buttonRow.Children.Add(openButton);

        var content = new StackPanel
        {
            Spacing = 14
        };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9
        });
        content.Children.Add(new TextBlock
        {
            Text = currentText,
            Opacity = 0.82
        });
        content.Children.Add(new TextBlock
        {
            Text = latestText,
            Opacity = 0.82,
            FontWeight = FontWeight.SemiBold
        });
        content.Children.Add(buttonRow);

        var surface = new Border
        {
            Background = ResolveBrush("SukiBackground", new SolidColorBrush(Color.Parse("#1C2438"))),
            BorderBrush = ResolveBrush("SukiBorderBrush", new SolidColorBrush(Color.Parse("#33415F"))),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24),
            Child = content
        };

        var window = new SukiWindow
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowTitlebarBackground = false,
            SystemDecorations = SystemDecorations.Full,
            Background = Brushes.Transparent,
            Foreground = ResolveBrush("SukiText", Brushes.White),
            Content = surface
        };

        openButton.Click += (_, _) =>
        {
            OpenUri(releaseUrl);
            window.Close();
        };

        laterButton.Click += (_, _) => window.Close();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner } lifetime &&
            lifetime.MainWindow == owner &&
            owner.IsVisible)
        {
            await window.ShowDialog(owner);
            return;
        }

        window.Show();
    }

    private static void OpenUri(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static IBrush ResolveBrush(string resourceKey, IBrush fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey, Application.Current.ActualThemeVariant, out var value) == true &&
            value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }
}
