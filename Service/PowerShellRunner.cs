using System.Diagnostics;
using System.Text;

namespace NetworkingTool.Service;

/// <summary>
/// The single chokepoint for invoking <c>powershell.exe</c>. The only process the service ever
/// starts is powershell.exe with a constant argument list and one <c>-Command</c> script; callers
/// build that script from validated/whitelisted values only (see <see cref="NetworkOperations"/> and
/// <see cref="AdapterService"/>). Mutation scripts are wrapped via <see cref="Wrap"/> so any error
/// surfaces as a non-zero exit code with a clean message; read-only enumeration scripts run raw so
/// their stdout (e.g. JSON) is preserved verbatim.
/// </summary>
internal static class PowerShellRunner
{
    /// <summary>
    /// Wraps a command sequence so any PowerShell error becomes a non-zero exit code and a clean
    /// message, rather than partial success with noise on stderr.
    /// </summary>
    public static string Wrap(string commands) =>
        $"$ErrorActionPreference='Stop'; try {{ {commands} Write-Output 'OK' }} catch {{ Write-Error $_.Exception.Message; exit 1 }}";

    public static (int exitCode, string stdout, string stderr) Run(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        ServiceLog.Write("RunPowerShell: " + script);

        using var process = new Process { StartInfo = psi };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(60000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            ServiceLog.Write("RunPowerShell: timed out after 60s, process killed.");
            return (1, stdoutBuilder.ToString(), "PowerShell command timed out.");
        }

        // Ensure all async output has been flushed after the process exited.
        process.WaitForExit();

        int exitCode = process.ExitCode;
        string stdout = stdoutBuilder.ToString();
        string stderr = stderrBuilder.ToString();

        ServiceLog.Write($"RunPowerShell result: exit={exitCode} stdoutLen={stdout.Length} stderrLen={stderr.Length}");

        return (exitCode, stdout, stderr);
    }
}
