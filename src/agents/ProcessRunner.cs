using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace CloudLiteIDE.Agents;

public static class ProcessRunner
{
    public sealed record ProcessExecutionResult(int ExitCode, bool TimedOut, bool OutputLimitHit);

    public static async ValueTask<ProcessExecutionResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        string? standardInput,
        int timeoutSeconds,
        int maxOutputBytes,
        StreamEventType stdoutType,
        StreamEventType stderrType,
        ChannelWriter<ExecutionEvent> streamWriter,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        long emittedBytes = 0;
        bool outputLimitHit = false;

        async Task EmitAsync(StreamEventType type, string line)
        {
            if (string.IsNullOrEmpty(line) || outputLimitHit)
            {
                return;
            }

            emittedBytes += Encoding.UTF8.GetByteCount(line + Environment.NewLine);
            if (emittedBytes > maxOutputBytes)
            {
                outputLimitHit = true;
                await streamWriter.WriteAsync(new ExecutionEvent(StreamEventType.Error, "Output truncated: max output size reached.", DateTimeOffset.UtcNow), cancellationToken);
                return;
            }

            await streamWriter.WriteAsync(new ExecutionEvent(type, line, DateTimeOffset.UtcNow), cancellationToken);
        }

        process.Start();

        var stdoutTask = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                await EmitAsync(stdoutType, line);
            }

            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                await EmitAsync(stdoutType, line);
            }
        }, cancellationToken);

        var stderrTask = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                await EmitAsync(stderrType, line);
            }

            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                await EmitAsync(stderrType, line);
            }
        }, cancellationToken);

        if (!string.IsNullOrEmpty(standardInput))
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
        }

        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProcessExecutionResult(-1, TimedOut: true, OutputLimitHit: outputLimitHit);
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessExecutionResult(process.ExitCode, TimedOut: false, OutputLimitHit: outputLimitHit);
    }
}
