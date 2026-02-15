using System;
using DiscordRPC;
using DiscordRPC.Logging;

namespace OnionHopV2.App.Services;

internal sealed class DiscordPresenceService : IDisposable
{
    private const string DiscordAppIdEnvironmentVariable = "ONIONHOP_DISCORD_APP_ID";
    private const string BuiltInDiscordAppId = "1470122484580880550";
    private DiscordRpcClient? _client;
    private bool _enabled;
    private bool _lastConnected;
    private string _lastExitLocation = "Automatic";

    public void SetEnabled(bool enabled, Action<string> log)
    {
        if (_enabled == enabled)
        {
            if (_enabled)
            {
                EnsureClient(log);
                TryApplyPresence(log);
            }
            return;
        }

        _enabled = enabled;
        if (!enabled)
        {
            try
            {
                _client?.ClearPresence();
            }
            catch
            {
            }

            _client?.Dispose();
            _client = null;
            return;
        }

        EnsureClient(log);
        TryApplyPresence(log);
    }

    public void Update(bool connected, string exitLocation, Action<string> log)
    {
        _lastConnected = connected;
        _lastExitLocation = string.IsNullOrWhiteSpace(exitLocation) ? "Automatic" : exitLocation;

        if (!_enabled)
        {
            return;
        }

        EnsureClient(log);
        TryApplyPresence(log);
    }

    private void EnsureClient(Action<string> log)
    {
        var appId = ResolveDiscordAppId();
        if (string.IsNullOrWhiteSpace(appId))
        {
            _client?.Dispose();
            _client = null;
            return;
        }

        if (_client?.IsInitialized == true)
        {
            return;
        }

        try
        {
            _client?.Dispose();
            _client = new DiscordRpcClient(appId)
            {
                Logger = new NullLogger()
            };

            if (_client.Initialize())
            {
            }
            else
            {
                _client?.Dispose();
                _client = null;
            }
        }
        catch (Exception)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    private string? ResolveDiscordAppId()
    {
        return NormalizeDiscordAppId(BuiltInDiscordAppId)
               ?? NormalizeDiscordAppId(Environment.GetEnvironmentVariable(DiscordAppIdEnvironmentVariable));
    }

    private static string? NormalizeDiscordAppId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                return null;
            }
        }

        return value.Length is >= 17 and <= 20 ? value : null;
    }

    private void TryApplyPresence(Action<string> log)
    {
        if (!_enabled || _client?.IsInitialized != true)
        {
            return;
        }

        try
        {
            var details = _lastConnected
                ? $"Connected to Tor - Exit: {_lastExitLocation}"
                : "Disconnected from Tor";

            var assets = new Assets
            {
                LargeImageKey = _lastConnected ? "connected" : "disconnected",
                LargeImageText = _lastConnected ? "Connected" : "Disconnected"
            };

            _client.SetPresence(new RichPresence
            {
                Details = details,
                State = _lastConnected ? "OnionHop V2" : "Idle",
                Timestamps = _lastConnected ? Timestamps.Now : null,
                Assets = assets
            });
        }
        catch (Exception)
        {
            // Silent failure as requested
        }
    }

    public void Dispose()
    {
        try
        {
            _client?.ClearPresence();
        }
        catch
        {
        }

        _client?.Dispose();
        _client = null;
    }
}
