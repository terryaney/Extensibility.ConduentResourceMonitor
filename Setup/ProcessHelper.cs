using System.Diagnostics;

namespace ConduentResourceMonitor.Setup;

internal static class ProcessHelper
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, (stdout + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string script)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"setup_{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(temp, script);
            return await RunAsync("powershell", $"-ExecutionPolicy Bypass -File \"{temp}\"");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // Runs a batch script elevated (UAC), waits for exit, returns exit code.
    // Output is shown in a visible cmd window; no capture is possible via runas.
    public static async Task<int> RunElevatedBatAsync(string script)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"setup_{Guid.NewGuid():N}.bat");
        File.WriteAllText(temp, "@echo off\r\n" + script + "\r\ndel \"%~f0\"");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{temp}\"")
            {
                Verb = "runas",
                UseShellExecute = true
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static async Task<bool> IsInPathAsync(string exe)
    {
        var (code, _) = await RunAsync("where", exe);
        return code == 0;
    }
}
