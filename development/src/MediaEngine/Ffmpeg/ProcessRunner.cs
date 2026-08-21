using System.Diagnostics;
using System.Text;

namespace VideoEditor.MediaEngine.Ffmpeg;

/// <summary>Result of a finished external process.</summary>
public record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Small async wrapper around external tools (ffmpeg/ffprobe).
/// Never blocks the calling thread; kills the process on cancellation.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(executable, arguments);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitAsync(process, cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs a process, handing every stdout line to a callback as it arrives —
    /// what ffmpeg's "-progress pipe:1" needs. The callback runs on a thread
    /// pool thread, so UI callers must marshal it themselves.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> onStandardOutputLine,
        CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(executable, arguments);
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onStandardOutputLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitAsync(process, cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, string.Empty, stderr.ToString());
    }

    /// <summary>Runs a process and returns raw bytes from stdout (for PCM / raw frames).</summary>
    public static async Task<(ProcessResult Result, byte[] Output)> RunBytesAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(executable, arguments);
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();

        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        await WaitAsync(process, cancellationToken).ConfigureAwait(false);

        return (new ProcessResult(process.ExitCode, string.Empty, stderr.ToString()), buffer.ToArray());
    }

    private static Process CreateProcess(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return new Process { StartInfo = info };
    }

    private static async Task WaitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* the process may have exited in the meantime */ }
            throw;
        }
    }
}
