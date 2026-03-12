using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OnionHopV2.Core.Platform.MacOS;

internal readonly record struct MacAuthorizationResult(bool Success, int ExitCode, string StdOut, string StdErr)
{
    public string FailureMessage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(StdErr))
            {
                return StdErr.Trim();
            }

            if (!string.IsNullOrWhiteSpace(StdOut))
            {
                return StdOut.Trim();
            }

            return $"Command failed with exit code {ExitCode}.";
        }
    }
}

internal static class MacAuthorization
{
    public static MacAuthorizationResult RunScript(string scriptContent, bool requireAdministrator, int timeoutMs = 120_000)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new MacAuthorizationResult(false, -1, string.Empty, "macOS authorization is only available on macOS.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "OnionHop", "mac-auth");
        Directory.CreateDirectory(tempDir);

        var scriptPath = Path.Combine(tempDir, $"cmd-{Guid.NewGuid():N}.sh");
        try
        {
            File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);

            return requireAdministrator
                ? RunWithAppleScript(scriptPath, timeoutMs)
                : RunDirect(scriptPath, timeoutMs);
        }
        catch (Exception ex)
        {
            return new MacAuthorizationResult(false, -1, string.Empty, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch
            {
            }
        }
    }

    public static string QuoteShellArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static MacAuthorizationResult RunDirect(string scriptPath, int timeoutMs)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(scriptPath);
        return RunProcess(psi, timeoutMs);
    }

    private static MacAuthorizationResult RunWithAppleScript(string scriptPath, int timeoutMs)
    {
        var appleScript = $"do shell script \"/bin/sh \" & quoted form of \"{EscapeAppleScriptString(scriptPath)}\" with administrator privileges";
        var psi = new ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(appleScript);
        return RunProcess(psi, timeoutMs);
    }

    private static MacAuthorizationResult RunProcess(ProcessStartInfo psi, int timeoutMs)
    {
        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return new MacAuthorizationResult(false, -1, string.Empty, "Failed to start process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new MacAuthorizationResult(false, -1, string.Empty, $"Command timed out after {timeoutMs} ms.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            return new MacAuthorizationResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new MacAuthorizationResult(false, -1, string.Empty, ex.Message);
        }
    }

    internal static string EscapeAppleScriptString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
