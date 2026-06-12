using System;
using OnionHopV3.Core.Security;
using Xunit;

namespace OnionHopV3.Tests.Security;

public sealed class SecretProtectorTests
{
    [Fact]
    public void Protect_then_Unprotect_round_trips()
    {
        var stored = SecretProtector.Protect("hunter2");
        Assert.Equal("hunter2", SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void Legacy_plaintext_is_returned_as_is()
    {
        // A value with no protection tag (an old settings file) must read back unchanged.
        Assert.Equal("legacy-value", SecretProtector.Unprotect("legacy-value"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_round_trips(string? value)
    {
        Assert.Equal(value, SecretProtector.Protect(value));
        Assert.Equal(value, SecretProtector.Unprotect(value));
    }

    [Fact]
    public void Protected_form_does_not_contain_plaintext_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is Windows-only; other platforms store plaintext by design.
        }

        var stored = SecretProtector.Protect("topsecret");
        Assert.NotNull(stored);
        Assert.DoesNotContain("topsecret", stored);
    }
}
