using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;

namespace OnionHopV3.Core.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsPersistentAdminHelper
{
    private const string TaskNamePrefix = "OnionHop Persistent Admin Helper ";

    public static bool IsEligibleInstallationPath(string? executablePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            var installDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(installDir) && File.Exists(Path.Combine(installDir, "unins000.exe")))
            {
                return true;
            }

            return IsUnderPath(fullPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"))
                   || IsUnderPath(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                   || IsUnderPath(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        }
        catch
        {
            return false;
        }
    }

    public static bool TryStartForCurrentUser(Action<string>? log = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            log?.Invoke("Persistent admin helper: current user SID was unavailable.");
            return false;
        }

        return TryStartForUserSid(sid, log);
    }

    public static bool TryStartForUserSid(string userSid, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return false;
        }

        return RunSchTasks($"/Run /TN \"{BuildTaskName(userSid)}\"", log, out _);
    }

    public static bool TryEnsureInstalled(
        string executablePath,
        string userSid,
        string userName,
        Action<string>? log,
        out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "Persistent admin helper is only supported on Windows.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            error = "Persistent admin helper install skipped because the executable path was invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(userSid))
        {
            error = "Persistent admin helper install skipped because the user SID was unavailable.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(userName))
        {
            error = "Persistent admin helper install skipped because the user name was unavailable.";
            return false;
        }

        if (!IsEligibleInstallationPath(executablePath))
        {
            error = "Persistent admin helper is only enabled for installed builds.";
            return false;
        }

        var tempScriptPath = Path.Combine(Path.GetTempPath(), $"onionhop-admin-helper-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(tempScriptPath, BuildRegistrationScript(), Encoding.UTF8);
            if (!RunPowerShellScript(
                    tempScriptPath,
                    BuildTaskName(userSid),
                    executablePath,
                    userName,
                    log,
                    out var createError))
            {
                error = string.IsNullOrWhiteSpace(createError)
                    ? "Persistent admin helper registration failed."
                    : createError;
                return false;
            }

            // Start immediately so the next GUI launch can reuse the helper without waiting for logon.
            TryStartForUserSid(userSid, log);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                File.Delete(tempScriptPath);
            }
            catch
            {
            }
        }
    }

    public static bool TryRemoveForCurrentUser(Action<string>? log = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            log?.Invoke("Persistent admin helper: current user SID was unavailable for removal.");
            return false;
        }

        return TryRemove(sid, log, out _);
    }

    public static bool TryRemove(string userSid, Action<string>? log, out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "Persistent admin helper is only supported on Windows.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(userSid))
        {
            error = "Persistent admin helper removal skipped because the user SID was unavailable.";
            return false;
        }

        // schtasks returns a non-zero exit code when the task does not exist. That is the desired end
        // state (no startup task), so treat "already gone" as success rather than an error.
        if (RunSchTasksAllowMissing($"/Delete /TN \"{BuildTaskName(userSid)}\" /F", log, out var deleteError))
        {
            return true;
        }

        error = deleteError;
        return false;
    }

    public static string BuildTaskName(string userSid)
    {
        var sidBytes = Encoding.UTF8.GetBytes(userSid);
        var hash = Convert.ToHexString(SHA256.HashData(sidBytes)).Substring(0, 12).ToLowerInvariant();
        return $"{TaskNamePrefix}{hash}";
    }

    private static string BuildRegistrationScript()
    {
        return """
$ErrorActionPreference = 'Stop'
$taskName = $args[0]
$exePath = $args[1]
$userName = $args[2]
$workingDir = Split-Path -Parent $exePath
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -ne $existing) {{
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}}
$action = New-ScheduledTaskAction -Execute $exePath -Argument '--helper-daemon' -WorkingDirectory $workingDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userName
$principal = New-ScheduledTaskPrincipal -UserId $userName -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Hidden
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Persistent elevated helper for OnionHop VPN and DNS operations.' -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
""";
    }

    private static bool RunPowerShellScript(
        string scriptPath,
        string taskName,
        string executablePath,
        string userName,
        Action<string>? log,
        out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {QuoteArg(scriptPath)} {QuoteArg(taskName)} {QuoteArg(executablePath)} {QuoteArg(userName)}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                error = "Failed to start powershell.exe.";
                return false;
            }

            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "powershell.exe timed out while registering the persistent admin helper task.";
                return false;
            }

            var stdOut = process.StandardOutput.ReadToEnd().Trim();
            var stdErr = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0)
            {
                if (!string.IsNullOrWhiteSpace(stdOut))
                {
                    log?.Invoke($"Persistent admin helper: {stdOut}");
                }

                return true;
            }

            error = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            if (!string.IsNullOrWhiteSpace(error))
            {
                log?.Invoke($"Persistent admin helper error: {error}");
            }

            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log?.Invoke($"Persistent admin helper exception: {ex.Message}");
            return false;
        }
    }

    private static bool RunSchTasks(string arguments, Action<string>? log, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo("schtasks", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                error = "Failed to start schtasks.exe.";
                return false;
            }

            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "schtasks.exe timed out.";
                return false;
            }

            var stdOut = process.StandardOutput.ReadToEnd().Trim();
            var stdErr = process.StandardError.ReadToEnd().Trim();

            if (process.ExitCode == 0)
            {
                if (!string.IsNullOrWhiteSpace(stdOut))
                {
                    log?.Invoke($"Persistent admin helper: {stdOut}");
                }

                return true;
            }

            error = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            if (!string.IsNullOrWhiteSpace(error))
            {
                log?.Invoke($"Persistent admin helper error: {error}");
            }

            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log?.Invoke($"Persistent admin helper exception: {ex.Message}");
            return false;
        }
    }

    // Like RunSchTasks, but used for /Delete: a non-zero exit code typically means the task does not
    // exist, which is the desired end state for removal. So missing-task failures are treated as
    // success and only a launch/timeout failure is reported as an error.
    private static bool RunSchTasksAllowMissing(string arguments, Action<string>? log, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo("schtasks", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                error = "Failed to start schtasks.exe.";
                return false;
            }

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "schtasks.exe timed out.";
                return false;
            }

            var stdOut = process.StandardOutput.ReadToEnd().Trim();
            var stdErr = process.StandardError.ReadToEnd().Trim();

            if (process.ExitCode == 0)
            {
                if (!string.IsNullOrWhiteSpace(stdOut))
                {
                    log?.Invoke($"Persistent admin helper: {stdOut}");
                }
            }
            else
            {
                // Non-zero exit on delete almost always means the task was already gone. Log for
                // diagnostics but report success so the caller treats the end state as clean.
                var detail = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                log?.Invoke($"Persistent admin helper: task already absent or could not be deleted ({detail}).");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log?.Invoke($"Persistent admin helper exception: {ex.Message}");
            return false;
        }
    }

    private static bool IsUnderPath(string candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var normalizedCandidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteArg(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
