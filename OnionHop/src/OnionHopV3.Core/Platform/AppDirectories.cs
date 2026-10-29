using System;

namespace OnionHopV3.Core.Platform;

/// <summary>
/// The per-user folders OnionHop keeps its data in. On Linux and macOS
/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns an empty string for a
/// folder that does not exist yet, such as ~/.config on a fresh account or a service user's new home.
/// The paths built from that were relative, so the data landed in the working directory, and where
/// that was not writable the CLI crashed at startup. These create the folder instead.
/// </summary>
public static class AppDirectories
{
    /// <summary>%APPDATA% on Windows, ~/.config (or $XDG_CONFIG_HOME) elsewhere.</summary>
    public static string RoamingData => Get(Environment.SpecialFolder.ApplicationData);

    /// <summary>%LOCALAPPDATA% on Windows, ~/.local/share (or $XDG_DATA_HOME) elsewhere.</summary>
    public static string LocalData => Get(Environment.SpecialFolder.LocalApplicationData);

    private static string Get(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);
        }
        catch
        {
            // A home that cannot be written still has a path; callers handle the write failing.
            return Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        }
    }
}
