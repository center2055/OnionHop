using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OnionHopV3.App.Services;

namespace OnionHopV3.App.Views;

public partial class AboutPageView : UserControl
{
    private static readonly Uri DiscordUri = new("https://discord.gg/y3MVspPzKQ");
    private static readonly Uri KoFiUri = new("https://ko-fi.com/center2055");
    private static readonly Uri TelegramUri = new("https://t.me/centerhop");
    private const string BitcoinAddress = "bc1q0gvnvrr0a64kpxylwgqkvlp5gt4c48jqxy9jy2";

    public AboutPageView()
    {
        InitializeComponent();
    }

    // Copy the Bitcoin donation address to the clipboard and briefly flash "Copied!" on the button.
    private async void OnCopyBitcoinClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardHelper.SetTextAsync(this, BitcoinAddress);
        }
        catch
        {
            return;
        }

        if (BitcoinCopyLabel is null)
        {
            return;
        }

        var original = BitcoinCopyLabel.Text;
        BitcoinCopyLabel.Text = "Copied!";
        try
        {
            await Task.Delay(1500);
        }
        finally
        {
            BitcoinCopyLabel.Text = original;
        }
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

    private void OnOpenTelegramClick(object? sender, RoutedEventArgs e)
    {
        OpenUri(TelegramUri);
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
