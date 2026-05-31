using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OnionHopV3.App.Views;

public partial class AboutPageView : UserControl
{
    private static readonly Uri DiscordUri = new("https://discord.gg/y3MVspPzKQ");
    private static readonly Uri KoFiUri = new("https://ko-fi.com/center2055");

    public AboutPageView()
    {
        InitializeComponent();
    }

    private void OnOpenReleasesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenUri(new Uri("https://github.com/center2055/OnionHop/issues"));
    }

    private void OnOpenDiscordClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenUri(DiscordUri);
    }

    private void OnOpenKoFiClick(object? sender, RoutedEventArgs e)
    {
        OpenUri(KoFiUri);
    }

    // Generic link button: opens the URL stored in the control's Tag.
    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url } &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            OpenUri(uri);
        }
    }

    private static void OpenUri(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
