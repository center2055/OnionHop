using System.Diagnostics;
using System.Text;

namespace OnionHopV3.Core.Platform;

internal static class ProcessOutputEncoding
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads the redirected output of a Go or Rust program (sing-box, xray, snowflake-proxy,
    /// lyrebird, ArtiHop, Arti, dnstt-client) as the UTF-8 it always writes. Left alone, .NET decodes
    /// it in the Windows code page, which turned the arrows in snowflake-proxy's traffic summary into
    /// "鈫?" on Chinese Windows (#91). System tools such as netsh do write in the code page, so they
    /// keep the default.
    /// </summary>
    public static ProcessStartInfo WithUtf8Output(this ProcessStartInfo psi)
    {
        if (psi.RedirectStandardOutput)
        {
            psi.StandardOutputEncoding = Utf8;
        }

        if (psi.RedirectStandardError)
        {
            psi.StandardErrorEncoding = Utf8;
        }

        return psi;
    }
}
