using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace HotspotKeeper.Services;

/// <summary>One-shot result of running hotspot.ps1.</summary>
public class PsResult
{
    public bool Ok { get; set; }
    public Dictionary<string, string> Values { get; } = new();
    public string? Error { get; set; }

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// Extracts the embedded PowerShell script once per run and invokes it
/// through Windows PowerShell 5.1 (always present on Windows 10/11).
/// </summary>
public static class Ps
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _scriptPath;

    public static string GetScriptPath()
    {
        if (_scriptPath != null) return _scriptPath;
        var asm = Assembly.GetExecutingAssembly();
        var resName = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("hotspot.ps1", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded hotspot.ps1 not found.");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HotspotKeeper");
        Directory.CreateDirectory(dir);
        _scriptPath = Path.Combine(dir, "hotspot.ps1");
        using var src = asm.GetManifestResourceStream(resName)!;
        using var dst = File.Create(_scriptPath);
        src.CopyTo(dst);
        return _scriptPath;
    }

    public static async Task<PsResult> RunAsync(string action, string? arg1 = null)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{GetScriptPath()}\" -Action {action}" +
                            (string.IsNullOrEmpty(arg1) ? "" : $" -Arg1 \"{arg1}\""),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            const int timeoutMs = 90_000;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new PsResult { Ok = false, Error = "操作超时（90 秒），已终止。" };
            }

            var stdout = await outTask;
            var stderr = await errTask;

            var result = new PsResult { Ok = p.ExitCode == 0 };
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("RESULT:"))
                {
                    var body = line[7..];
                    var eq = body.IndexOf('=');
                    if (eq > 0) result.Values[body[..eq]] = body[(eq + 1)..];
                    else if (body.Length > 0) result.Values[body] = "1";
                }
                else if (line.StartsWith("ERR:"))
                {
                    result.Error = line[4..];
                    result.Ok = false;
                }
            }

            if (result.Error == null && !string.IsNullOrWhiteSpace(stderr))
                result.Error = stderr.Trim();
            if (result.Error == null && !result.Ok)
                result.Error = $"PowerShell 退出码 {p.ExitCode}";
            if (result.Error != null) result.Ok = false;
            return result;
        }
        catch (Exception ex)
        {
            return new PsResult { Ok = false, Error = ex.Message };
        }
        finally
        {
            Gate.Release();
        }
    }
}
