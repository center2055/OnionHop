using System.Runtime.InteropServices;
using System.Text;

namespace OnionHopV3.Core.Platform.Windows;

/// <summary>
/// Reads a process's executable path with the least access Windows offers for it
/// (PROCESS_QUERY_LIMITED_INFORMATION). <see cref="System.Diagnostics.Process.MainModule"/> needs
/// memory-read access instead, and Arti hardens its process against exactly that, so for arti.exe
/// MainModule always fails.
/// </summary>
internal static class WindowsProcessPath
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    internal static string? Get(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
