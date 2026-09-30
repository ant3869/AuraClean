using System.Diagnostics;
using System.IO;
using System.Text;

namespace AuraClean.Helpers;

/// <summary>
/// Runs console tools without deadlocking on redirected pipes: stdout and stderr are drained
/// concurrently, cancellation kills the process tree, and system tools are always resolved
/// from System32 so a planted executable next to AuraClean can never be picked up instead.
/// </summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Succeeded => ExitCode == 0;

        /// <summary>stderr when present, otherwise stdout — trimmed for display.</summary>
        public string CombinedMessage =>
            (string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError).Trim();
    }

    /// <summary>Absolute path of a tool that ships in %SystemRoot%\System32.</summary>
    public static string SystemTool(string exeName) => Path.Combine(Environment.SystemDirectory, exeName);

    /// <summary>
    /// Starts <paramref name="fileName"/> hidden, captures its output, and waits for it to exit.
    /// </summary>
    /// <param name="onOutputLine">Optional callback invoked for every stdout line as it arrives.</param>
    /// <param name="killOnCancel">
    /// When true, cancellation terminates the process tree. Set to false for tools that must not be
    /// interrupted mid-operation (the wait is abandoned but the process keeps running).
    /// </param>
    /// <param name="timeout">Optional hard limit after which the process tree is terminated.</param>
    public static async Task<Result> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default,
        Action<string>? onOutputLine = null,
        bool killOnCancel = true,
        TimeSpan? timeout = null,
        Encoding? outputEncoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (outputEncoding != null)
        {
            psi.StandardOutputEncoding = outputEncoding;
            psi.StandardErrorEncoding = outputEncoding;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sync = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (sync) stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (sync) stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {Path.GetFileName(fileName)}.");

        // Tools that prompt (e.g. winget agreements) must see EOF instead of hanging forever.
        try { process.StandardInput.Close(); }
        catch (IOException ex)
        {
            // Process already exited.
            System.Diagnostics.Debug.WriteLine($"[AuraClean] Stdin close raced process exit for {fileName}: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linked = timeoutCts != null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            bool timedOut = timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested;
            if (killOnCancel || timedOut)
                TryKill(process);

            if (timedOut)
                throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout!.Value.TotalMinutes:F0} minute(s).");

            throw;
        }

        // The parameterless overload also waits for the redirected streams to reach EOF,
        // guaranteeing every output line has been captured. The process has already exited.
        process.WaitForExit();

        lock (sync)
            return new Result(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            DiagnosticLogger.Warn("ProcessRunner", $"Could not terminate {process.StartInfo.FileName}", ex);
        }
    }
}
