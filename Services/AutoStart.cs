using System.Diagnostics;

namespace HotspotKeeper.Services;

/// <summary>
/// Boot auto-start via Task Scheduler (runs with highest privileges, no UAC prompt at logon).
/// </summary>
public static class AutoStart
{
    private const string TaskName = "HotspotKeeper";

    public static async Task<bool> IsEnabledAsync()
    {
        var (code, _) = await RunAsync("schtasks.exe", $"/Query /TN {TaskName}").ConfigureAwait(false);
        return code == 0;
    }

    public static async Task<(bool ok, string error)> SetEnabledAsync(bool enable)
    {
        if (enable)
        {
            var exe = Environment.ProcessPath ?? "";
            var (code, output) = await RunAsync("schtasks.exe",
                $"/Create /F /TN {TaskName} /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\"").ConfigureAwait(false);
            return (code == 0, output);
        }
        else
        {
            var (code, output) = await RunAsync("schtasks.exe",
                $"/Delete /F /TN {TaskName}").ConfigureAwait(false);
            return (code == 0, output);
        }
    }

    private static async Task<(int code, string output)> RunAsync(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var outp = await p.StandardOutput.ReadToEndAsync();
            var errp = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, (outp + errp).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
