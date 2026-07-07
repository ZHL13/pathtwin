using System.Diagnostics;
using System.Text;
using PathTwin.App.Constants;

namespace PathTwin.App.Platform;

public sealed class TaskSchedulerService
{
    private static readonly string TaskName = $"{AppConstants.ApplicationName}_AutoStart";

    public async Task<TaskSchedulerResult> CreateOrUpdateAsync(string exePath, bool startOnLogon, bool startOnUnlock)
    {
        if (!OperatingSystem.IsWindows())
            return TaskSchedulerResult.Fail("Task Scheduler is only supported on Windows.");

        try
        {
            var existing = await IsRegisteredAsync();
            var verb = existing ? "Updated" : "Created";
            var script = BuildRegistrationScript(exePath, existing, startOnLogon, startOnUnlock);
            var (ok, output) = await RunElevatedAsync(script);

            if (!ok)
                return TaskSchedulerResult.Fail(output);
            if (output.Contains("DENIED", StringComparison.OrdinalIgnoreCase))
                return TaskSchedulerResult.Fail("Administrator permission required. Please click Yes on the UAC prompt.");

            return TaskSchedulerResult.Ok($"Task '{TaskName}' {verb.ToLowerInvariant()}.");
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Contains("denied", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                return TaskSchedulerResult.Fail("Administrator permission was denied or the UAC prompt was cancelled.");

            return TaskSchedulerResult.Fail(msg);
        }
    }

    public async Task<TaskSchedulerResult> DeleteAsync()
    {
        if (!OperatingSystem.IsWindows())
            return TaskSchedulerResult.Fail("Task Scheduler is only supported on Windows.");

        try
        {
            var script = JoinLines(
                $"$tn = '{TaskName}'",
                "$e = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue",
                "if ($e) { Unregister-ScheduledTask -TaskName $tn -Confirm:$false; Write-Output 'DELETED' }",
                "else { Write-Output 'NOT_FOUND' }"
            );
            var (_, output) = await RunElevatedAsync(script);
            return TaskSchedulerResult.Ok(output.Contains("NOT_FOUND") ? "No task to delete." : "Task deleted.");
        }
        catch (Exception ex)
        {
            return TaskSchedulerResult.Fail(ex.Message);
        }
    }

    public async Task<bool> IsRegisteredAsync()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var (_, output) = await RunAsync(JoinLines(
                $"$e = Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue",
                "if ($e) { Write-Output 'REGISTERED' } else { Write-Output 'NOT_FOUND' }"
            ));
            return output.Contains("REGISTERED", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task<TaskSchedulerResult> TestRunAsync()
    {
        if (!OperatingSystem.IsWindows())
            return TaskSchedulerResult.Fail("Task Scheduler is only supported on Windows.");

        try
        {
            var (ok, output) = await RunAsync(JoinLines(
                $"$t = Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue",
                "if (-not $t) { Write-Output 'NOT_FOUND'; exit 1 }",
                $"Start-ScheduledTask -TaskName '{TaskName}'",
                "Write-Output 'STARTED'"
            ));
            if (!ok) return TaskSchedulerResult.Fail(output);
            return TaskSchedulerResult.Ok(output.Contains("NOT_FOUND") ? "Task not found. Create it first." : "Task started.");
        }
        catch (Exception ex) { return TaskSchedulerResult.Fail(ex.Message); }
    }

    // ---- script builders ----

    private static string BuildRegistrationScript(string exePath, bool existing, bool startOnLogon, bool startOnUnlock)
    {
        var escapedExe = exePath.Replace("'", "''");

        // Build trigger array. -AtSessionUnlock doesn't exist in WinPS 5.1; use CIM.
        var triggerLines = new List<string> { "  $triggers = @()" };
        if (startOnLogon)
            triggerLines.Add("  $triggers += New-ScheduledTaskTrigger -AtLogOn");
        if (startOnUnlock)
        {
            triggerLines.Add("  $cimClass = Get-CimClass MSFT_TaskSessionStateChangeTrigger -Namespace Root/Microsoft/Windows/TaskScheduler");
            triggerLines.Add("  $unlock = New-CimInstance -CimClass $cimClass -ClientOnly");
            triggerLines.Add("  $unlock.StateChange = 8");
            triggerLines.Add("  $unlock.UserId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name");
            triggerLines.Add("  $triggers += $unlock");
        }
        if (!startOnLogon && !startOnUnlock)
            triggerLines.Add("  $triggers += New-ScheduledTaskTrigger -AtLogOn"); // fallback

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "try {",
            $"  $tn = '{TaskName}'",
            $"  $a = New-ScheduledTaskAction -Execute '{escapedExe}' -Argument '--auto'"
        };
        lines.AddRange(triggerLines);
        lines.AddRange(new[]
        {
            "  $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew",
            "  $u = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name",
            "  $p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Limited"
        });

        if (existing)
        {
            lines.Add("  Set-ScheduledTask -TaskName $tn -Action $a -Trigger $triggers -Settings $s -Principal $p");
            lines.Add("  Write-Output 'UPDATED'");
        }
        else
        {
            lines.Add("  Register-ScheduledTask -TaskName $tn -Action $a -Trigger $triggers -Settings $s -Principal $p -Force");
            lines.Add("  Write-Output 'CREATED'");
        }

        lines.Add("} catch {");
        lines.Add("  Write-Output \"DENIED: $($_.Exception.Message)\"");
        lines.Add("  exit 1");
        lines.Add("}");
        return JoinLines(lines);
    }

    // ---- process runners ----

    /// <summary>Runs a PowerShell script with admin elevation (triggers UAC).</summary>
    private static async Task<(bool Success, string Output)> RunElevatedAsync(string script)
    {
        var scriptFile = Path.GetTempFileName() + ".ps1";
        var outputFile = Path.GetTempFileName() + ".txt";
        try
        {
            var wrapped = $"$out = '{outputFile.Replace("'", "''")}'\n& {{\n{script}\n}} *> $out";
            await File.WriteAllTextAsync(scriptFile, wrapped, Encoding.UTF8);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync();

            var output = File.Exists(outputFile) ? await File.ReadAllTextAsync(outputFile, Encoding.UTF8) : "(no output)";
            var ok = process.ExitCode == 0 && !output.Contains("DENIED:", StringComparison.OrdinalIgnoreCase);
            return (ok, output.Trim());
        }
        finally { TryDelete(scriptFile); TryDelete(outputFile); }
    }

    /// <summary>Runs a PowerShell script without elevation.</summary>
    private static async Task<(bool Success, string Output)> RunAsync(string script)
    {
        var scriptFile = Path.GetTempFileName() + ".ps1";
        try
        {
            await File.WriteAllTextAsync(scriptFile, script, Encoding.UTF8);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var output = (stdout + stderr).Trim();
            return (process.ExitCode == 0 && string.IsNullOrWhiteSpace(stderr), output);
        }
        finally { TryDelete(scriptFile); }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static string JoinLines(params string[] lines) => string.Join("\n", lines);
    private static string JoinLines(IEnumerable<string> lines) => string.Join("\n", lines);
}

public sealed class TaskSchedulerResult
{
    public bool Success { get; private init; }
    public string Message { get; private init; } = string.Empty;
    public static TaskSchedulerResult Ok(string m) => new() { Success = true, Message = m };
    public static TaskSchedulerResult Fail(string m) => new() { Success = false, Message = m };
}
