using System;
using System.IO;
using OnionHopV3.Core.Tor;
using Xunit;

namespace OnionHopV3.Tests.Tor;

/// <summary>
/// Guards the pluggable-transport exec-path handling. Tor's ClientTransportPlugin parser splits on
/// whitespace and ignores quotes, so the exec path MUST be space-free or the managed proxy fails to
/// launch ("CreateProcessA() failed") and every bridge breaks — the v3 regression hit by users whose
/// Windows folder has a space (e.g. "C:\Users\First Last") with 8.3 short names disabled (#51).
/// </summary>
public sealed class PluggableTransportPathTests
{
    [Fact]
    public void MakeExecutablePathTokenSafe_NoSpace_ReturnsUnchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onionhop-nospace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "lyrebird.exe");
        File.WriteAllText(exe, "stub");

        Assert.Equal(exe, TorBridgeManager.MakeExecutablePathTokenSafe(exe));
    }

    [Fact]
    public void MakeExecutablePathTokenSafe_PathWithSpace_BecomesSpaceFreeAndExists()
    {
        // A directory with a space mimics "C:\Users\First Last\...".
        var dir = Path.Combine(Path.GetTempPath(), "onionhop space " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "lyrebird.exe");
        File.WriteAllText(exe, "stub");
        Assert.Contains(' ', exe);

        var safe = TorBridgeManager.MakeExecutablePathTokenSafe(exe);

        // The whole point: Tor must receive a token with no space, pointing at a real file.
        Assert.False(safe.Contains(' '), $"Expected a space-free path, got: {safe}");
        Assert.True(File.Exists(safe), $"Space-free path does not exist: {safe}");
    }
}
