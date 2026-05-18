using System.Threading.Channels;

namespace CloudLiteIDE.Agents;

public sealed class NativeExecutor : IExecutor
{
    private readonly ExecutorSettings _settings;
    private readonly string _sessionRoot;

    public NativeExecutor(Guid executorId, ExecutorSettings settings)
    {
        ExecutorId = executorId;
        _settings = settings;
        AnonymousToken = Guid.NewGuid().ToString("N");
        _sessionRoot = Path.Combine(Path.GetTempPath(), "cloudliteide", AnonymousToken);
        Directory.CreateDirectory(_sessionRoot);
    }

    public Guid ExecutorId { get; }
    public string AnonymousToken { get; }

    public async ValueTask<ExecutionResult> ExecuteAsync(ExecuteRequest request, ChannelWriter<ExecutionEvent> streamWriter, CancellationToken cancellationToken)
    {
        var runDir = Path.Combine(_sessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);

        if (request.Language.Equals("python", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecutePythonAsync(request, runDir, streamWriter, cancellationToken);
        }

        return await ExecuteCppAsync(request, runDir, streamWriter, cancellationToken);
    }

    private async ValueTask<ExecutionResult> ExecutePythonAsync(ExecuteRequest request, string runDir, ChannelWriter<ExecutionEvent> streamWriter, CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(runDir, "main.py");
        await File.WriteAllTextAsync(sourcePath, request.Code, cancellationToken);

        await streamWriter.WriteAsync(new ExecutionEvent(StreamEventType.Status, "Running Python program...", DateTimeOffset.UtcNow), cancellationToken);

        var python = await TryResolveCommandAsync(["python3", "python"], runDir, cancellationToken);
        if (python is null)
        {
            return new ExecutionResult(false, "Python runtime not found.");
        }

        var result = await ProcessRunner.RunAsync(
            python,
            [sourcePath],
            runDir,
            request.Stdin,
            _settings.RunTimeoutSeconds,
            _settings.MaxOutputBytes,
            StreamEventType.RuntimeStdout,
            StreamEventType.RuntimeStderr,
            streamWriter,
            cancellationToken);

        if (result.TimedOut)
        {
            return new ExecutionResult(false, "Runtime timeout reached.");
        }

        if (result.OutputLimitHit)
        {
            return new ExecutionResult(false, "Runtime output exceeded limit.", result.ExitCode);
        }

        return result.ExitCode == 0
            ? new ExecutionResult(true, "Execution finished successfully.", result.ExitCode)
            : new ExecutionResult(false, "Execution failed.", result.ExitCode);
    }

    private async ValueTask<ExecutionResult> ExecuteCppAsync(ExecuteRequest request, string runDir, ChannelWriter<ExecutionEvent> streamWriter, CancellationToken cancellationToken)
    {
        var options = request.CppOptions ?? new CompilerOptions();
        var sourcePath = Path.Combine(runDir, "main.cpp");
        var binaryPath = Path.Combine(runDir, OperatingSystem.IsWindows() ? "main.exe" : "main");

        await File.WriteAllTextAsync(sourcePath, request.Code, cancellationToken);

        await streamWriter.WriteAsync(new ExecutionEvent(StreamEventType.Status, "Compiling C++ program...", DateTimeOffset.UtcNow), cancellationToken);

        var compileResult = await ProcessRunner.RunAsync(
            options.Compiler,
            BuildCompileArgs(options, sourcePath, binaryPath),
            runDir,
            null,
            _settings.CompileTimeoutSeconds,
            _settings.MaxOutputBytes,
            StreamEventType.CompileStdout,
            StreamEventType.CompileStderr,
            streamWriter,
            cancellationToken);

        if (compileResult.TimedOut)
        {
            return new ExecutionResult(false, "Compilation timeout reached.");
        }

        if (compileResult.OutputLimitHit)
        {
            return new ExecutionResult(false, "Compilation output exceeded limit.", compileResult.ExitCode);
        }

        if (compileResult.ExitCode != 0)
        {
            return new ExecutionResult(false, "Compilation failed.", compileResult.ExitCode);
        }

        await streamWriter.WriteAsync(new ExecutionEvent(StreamEventType.Status, "Running compiled binary...", DateTimeOffset.UtcNow), cancellationToken);

        var runtimeResult = await ProcessRunner.RunAsync(
            binaryPath,
            Array.Empty<string>(),
            runDir,
            request.Stdin,
            _settings.RunTimeoutSeconds,
            _settings.MaxOutputBytes,
            StreamEventType.RuntimeStdout,
            StreamEventType.RuntimeStderr,
            streamWriter,
            cancellationToken);

        if (runtimeResult.TimedOut)
        {
            return new ExecutionResult(false, "Runtime timeout reached.");
        }

        if (runtimeResult.OutputLimitHit)
        {
            return new ExecutionResult(false, "Runtime output exceeded limit.", runtimeResult.ExitCode);
        }

        return runtimeResult.ExitCode == 0
            ? new ExecutionResult(true, "Execution finished successfully.", runtimeResult.ExitCode)
            : new ExecutionResult(false, "Execution failed.", runtimeResult.ExitCode);
    }

    private static IEnumerable<string> BuildCompileArgs(CompilerOptions options, string sourcePath, string outputPath)
    {
        yield return $"-std={options.Standard}";
        yield return $"-{options.Optimization}";

        if (!options.WarningLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"-{options.WarningLevel}";
        }

        yield return sourcePath;
        yield return "-o";
        yield return outputPath;
    }

    private static async ValueTask<string?> TryResolveCommandAsync(IEnumerable<string> candidates, string workDir, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                var result = await ProcessRunner.RunAsync(
                    candidate,
                    ["--version"],
                    workDir,
                    null,
                    timeoutSeconds: 3,
                    maxOutputBytes: 4_096,
                    StreamEventType.Status,
                    StreamEventType.Status,
                    Channel.CreateUnbounded<ExecutionEvent>().Writer,
                    cancellationToken);

                if (!result.TimedOut && result.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // candidate not available
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sessionRoot))
            {
                Directory.Delete(_sessionRoot, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
