using System;
using System.IO;
using System.Linq;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// Publishing an onion service through Tor's control port (#77). The address is derived from the
/// key, so the rules that matter are: ask Tor for a new key only the first time, always reuse the
/// saved one afterwards, and never lose a key, because losing it means losing the address someone
/// was already given.
/// </summary>
public sealed class OnionServiceTests
{
    [Fact]
    public void Parses_the_address_and_key_from_an_add_onion_reply()
    {
        var response = string.Join("\n",
            "250-ServiceID=vv6ozcdqcvxgtvhwvbhrbmwbxzstqzcqjfsvhtj4dqbjhqcbxzsvfnyd",
            "250-PrivateKey=ED25519-V3:0H8lQ2sVzC5xg2A7pQ==",
            "250 OK");

        var (serviceId, privateKey) = TorService.ParseAddOnionResponse(response);

        Assert.Equal("vv6ozcdqcvxgtvhwvbhrbmwbxzstqzcqjfsvhtj4dqbjhqcbxzsvfnyd", serviceId);
        Assert.Equal("ED25519-V3:0H8lQ2sVzC5xg2A7pQ==", privateKey);
    }

    [Fact]
    public void Parses_a_reply_that_carries_no_key()
    {
        // Re-publishing a saved key returns only the address, so the caller must keep its own key.
        var response = string.Join("\n", "250-ServiceID=abcdef", "250 OK");

        var (serviceId, privateKey) = TorService.ParseAddOnionResponse(response);

        Assert.Equal("abcdef", serviceId);
        Assert.Null(privateKey);
    }

    [Fact]
    public void Asks_tor_for_a_new_key_only_when_there_is_none()
    {
        var fresh = TorService.BuildAddOnionCommand(null, 80, "192.168.1.50", 8080);
        var reused = TorService.BuildAddOnionCommand("ED25519-V3:KEY", 80, "192.168.1.50", 8080);

        Assert.Contains("NEW:ED25519-V3", fresh);
        Assert.Contains("ED25519-V3:KEY", reused);
        Assert.DoesNotContain("NEW:", reused);
    }

    [Fact]
    public void Detaches_the_service_so_it_outlives_the_control_connection()
    {
        // We open a control connection, publish, and immediately QUIT. Without Detach the service
        // would be torn down the moment we disconnect, so it would never actually be reachable.
        Assert.Contains("Flags=Detach", TorService.BuildAddOnionCommand(null, 80, "127.0.0.1", 80));
    }

    [Fact]
    public void Maps_the_onion_port_to_the_target_address()
    {
        var command = TorService.BuildAddOnionCommand(null, 80, "192.168.1.50", 8080);

        Assert.Contains("Port=80,192.168.1.50:8080", command);
    }

    [Fact]
    public void Falls_back_to_loopback_when_no_target_host_is_given()
    {
        Assert.Contains("Port=443,127.0.0.1:443", TorService.BuildAddOnionCommand(null, 443, "  ", 443));
    }
}

/// <summary>Persistence of the user's onion services, keys included.</summary>
public sealed class OnionServiceStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly OnionServiceStore _store;

    public OnionServiceStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"onionhop-svc-{Guid.NewGuid():N}");
        _store = new OnionServiceStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Round_trips_a_service_including_its_key()
    {
        _store.Add(new OnionService
        {
            Label = "Camera",
            OnionPort = 80,
            TargetHost = "192.168.1.50",
            TargetPort = 8080,
            PrivateKey = "ED25519-V3:SECRET",
            Address = "abcdef"
        });

        var loaded = _store.Load().Single();

        Assert.Equal("Camera", loaded.Label);
        Assert.Equal(8080, loaded.TargetPort);
        Assert.Equal("ED25519-V3:SECRET", loaded.PrivateKey);
        Assert.Equal("abcdef.onion", loaded.Hostname);
    }

    [Fact]
    public void Encrypts_the_key_at_rest_where_a_backend_exists()
    {
        // SecretProtector only has a DPAPI backend, so Windows is the only platform where the key is
        // actually encrypted on disk. Asserting that everywhere would be asserting a guarantee the
        // app does not make; the Unix side is covered by the file-mode test below instead.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _store.Add(new OnionService { Label = "Camera", PrivateKey = "ED25519-V3:TOPSECRET" });

        Assert.DoesNotContain("TOPSECRET", File.ReadAllText(_store.StorePath));
    }

    [Fact]
    public void Keeps_the_file_readable_by_its_owner_only_on_unix()
    {
        // Where the key is stored in plaintext, the file mode is the only thing protecting it from
        // other local accounts.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _store.Add(new OnionService { Label = "Camera", PrivateKey = "ED25519-V3:TOPSECRET" });

        var mode = File.GetUnixFileMode(_store.StorePath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Update_keeps_the_stored_key_when_the_edit_does_not_carry_one()
    {
        // Editing a label must never silently change the address the user already handed out.
        var added = _store.Add(new OnionService { Label = "Camera", PrivateKey = "ED25519-V3:SECRET" });

        _store.Update(new OnionService { Id = added.Id, Label = "Front door", PrivateKey = null });

        var loaded = _store.Load().Single();
        Assert.Equal("Front door", loaded.Label);
        Assert.Equal("ED25519-V3:SECRET", loaded.PrivateKey);
    }

    [Fact]
    public void Remove_deletes_only_the_named_service()
    {
        var a = _store.Add(new OnionService { Label = "A" });
        _store.Add(new OnionService { Label = "B" });

        _store.Remove(a.Id);

        Assert.Equal("B", _store.Load().Single().Label);
    }

    [Fact]
    public void A_corrupt_file_is_quarantined_rather_than_deleted()
    {
        // The file holds private keys: starting fresh is fine, destroying them is not.
        File.WriteAllText(_store.StorePath, "{ not json");

        Assert.Empty(_store.Load());
        Assert.NotEmpty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void Missing_file_loads_as_empty()
    {
        Assert.Empty(_store.Load());
    }
}
