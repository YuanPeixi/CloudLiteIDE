using System.Text;
using System.Threading.Channels;

namespace CloudLiteIDE.Agents;

public sealed class SandboxieExecutor : IExecutor
{
    private readonly ExecutorSettings _settings;
    private readonly string _sessionRoot;
    private readonly string _boxName;
    private readonly SemaphoreSlim _provisionLock = new(1, 1);
    private bool _provisioned;

    public SandboxieExecutor(Guid executorId, ExecutorSettings settings)
    {
        ExecutorId = executorId;
        _settings = settings;
        AnonymousToken = Guid.NewGuid().ToString("N");
        _boxName = string.IsNullOrWhiteSpace(settings.SandboxieBoxName) ? AnonymousToken : settings.SandboxieBoxName;
        _sessionRoot = CreateSessionRoot(AnonymousToken);
        Directory.CreateDirectory(_sessionRoot);
    }

    public Guid ExecutorId { get; }
    public string AnonymousToken { get; }

    public async ValueTask<ExecutionResult> ExecuteAsync(ExecuteRequest request, ChannelWriter<ExecutionEvent> streamWriter, CancellationToken cancellationToken)
    {
        var provisionResult = await EnsureProvisionedAsync(streamWriter, cancellationToken);
        if (!provisionResult.Success)
        {
            return provisionResult;
        }

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

        await streamWriter.WriteAsync(new ExecutionEvent(StreamEventType.Status, "Running Python program in Sandboxie...", DateTimeOffset.UtcNow), cancellationToken);

        var python = await TryResolveCommandAsync(["python3", "python"], runDir, cancellationToken);
        if (python is null)
        {
            return new ExecutionResult(false, "Python runtime not found.");
        }

        var result = await RunInSandboxAsync(
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

        await streamWriter.WriteAsync(new ExecutionEvent(StreamEventType.Status, "Compiling C++ program in Sandboxie...", DateTimeOffset.UtcNow), cancellationToken);

        var compileResult = await RunInSandboxAsync(
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

        await streamWriter.WriteAsync(new ExecutionEvent(StreamEventType.Status, "Running compiled binary in Sandboxie...", DateTimeOffset.UtcNow), cancellationToken);

        var runtimeResult = await RunInSandboxAsync(
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

    private async ValueTask<ProcessRunner.ProcessExecutionResult> RunInSandboxAsync(
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
        return await ProcessRunner.RunAsync(
            ResolveSandboxieStartExePath(),
            BuildSandboxieArgs(fileName, arguments),
            workingDirectory,
            standardInput,
            timeoutSeconds,
            maxOutputBytes,
            stdoutType,
            stderrType,
            streamWriter,
            cancellationToken);
    }

    private IEnumerable<string> BuildSandboxieArgs(string fileName, IEnumerable<string> arguments)
    {
        yield return $"/box:{_boxName}";
        yield return "/silent";
        yield return "/wait";
        yield return fileName;

        foreach (var argument in arguments)
        {
            yield return argument;
        }
    }

    private async ValueTask<ExecutionResult> EnsureProvisionedAsync(ChannelWriter<ExecutionEvent> streamWriter, CancellationToken cancellationToken)
    {
        if (_provisioned)
        {
            return new ExecutionResult(true, "Sandboxie template is ready.");
        }

        await _provisionLock.WaitAsync(cancellationToken);
        try
        {
            if (_provisioned)
            {
                return new ExecutionResult(true, "Sandboxie template is ready.");
            }

            var templateName = _settings.SandboxieTemplateBoxName;
            var iniPath = ResolveSandboxieIniPath();
            if (iniPath is null)
            {
                return new ExecutionResult(false, "Sandboxie.ini was not found.");
            }

            await CloneSandboxSectionAsync(iniPath, templateName, _boxName, cancellationToken);
            await ReloadSandboxieConfigAsync(cancellationToken);
            _provisioned = true;
            await streamWriter.WriteAsync(new ExecutionEvent(StreamEventType.Status, $"Sandboxie template '{templateName}' cloned into '{_boxName}'.", DateTimeOffset.UtcNow), cancellationToken);
            return new ExecutionResult(true, "Sandboxie template provisioned.");
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    private static string? ResolveSandboxieIniPath()
    {
        var windowsIni = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sandboxie.ini");
        if (File.Exists(windowsIni))
        {
            return windowsIni;
        }

        var startExe = ResolveSandboxieStartExePathStatic();
        var installDir = Path.GetDirectoryName(startExe);
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            var installIni = Path.Combine(installDir, "Sandboxie.ini");
            if (File.Exists(installIni))
            {
                return installIni;
            }
        }

        return null;
    }

    private static async ValueTask CloneSandboxSectionAsync(string iniPath, string templateName, string targetName, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(iniPath, cancellationToken);
        var updatedLines = ReplaceOrAppendSection(lines, templateName, targetName);
        await File.WriteAllLinesAsync(iniPath, updatedLines, Encoding.Unicode, cancellationToken);
    }

    private static IEnumerable<string> ReplaceOrAppendSection(IReadOnlyList<string> lines, string templateName, string targetName)
    {
        var templateLines = ExtractSection(lines, templateName).ToList();
        if (templateLines.Count == 0)
        {
            throw new InvalidOperationException($"Sandboxie template section '{templateName}' was not found.");
        }

        templateLines[0] = $"[{targetName}]";
        RemoveSection(lines, targetName, out var withoutTarget);
        return AppendSection(withoutTarget, templateLines);
    }

    private static IEnumerable<string> AppendSection(IEnumerable<string> source, IReadOnlyList<string> sectionLines)
    {
        foreach (var line in source)
        {
            yield return line;
        }

        if (source is ICollection<string> collection && collection.Count > 0)
        {
            yield return string.Empty;
        }

        foreach (var line in sectionLines)
        {
            yield return line;
        }
    }

    private static IEnumerable<string> ExtractSection(IReadOnlyList<string> lines, string sectionName)
    {
        var sectionHeader = $"[{sectionName}]";
        var inSection = false;

        foreach (var line in lines)
        {
            if (!inSection)
            {
                if (string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    yield return line;
                }

                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.Trim().EndsWith("]", StringComparison.Ordinal))
            {
                yield break;
            }

            yield return line;
        }
    }

    private static void RemoveSection(IReadOnlyList<string> lines, string sectionName, out List<string> result)
    {
        result = new List<string>();
        var sectionHeader = $"[{sectionName}]";
        var inSection = false;

        foreach (var line in lines)
        {
            if (!inSection)
            {
                if (string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                }
                else
                {
                    result.Add(line);
                }

                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.Trim().EndsWith("]", StringComparison.Ordinal))
            {
                inSection = false;
                result.Add(line);
            }
        }
    }

    private async ValueTask ReloadSandboxieConfigAsync(CancellationToken cancellationToken)
    {
        var startExe = ResolveSandboxieStartExePath();
        var workingDirectory = Path.GetDirectoryName(startExe) ?? Environment.CurrentDirectory;

        var reloadResult = await ProcessRunner.RunAsync(
            startExe,
            ["/reload"],
            workingDirectory,
            null,
            timeoutSeconds: 10,
            maxOutputBytes: 4_096,
            StreamEventType.Status,
            StreamEventType.Status,
            Channel.CreateUnbounded<ExecutionEvent>().Writer,
            cancellationToken);

        if (reloadResult.ExitCode != 0 && !reloadResult.TimedOut)
        {
            throw new InvalidOperationException("Sandboxie configuration reload failed.");
        }
    }

    private static string ResolveSandboxieStartExePathStatic()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            var candidate = Path.Combine(programFiles, "Sandboxie-Plus", "Start.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(programFiles, "Sandboxie", "Start.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            var candidate = Path.Combine(programFilesX86, "Sandboxie-Plus", "Start.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(programFilesX86, "Sandboxie", "Start.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "Start.exe";
    }

    private string ResolveSandboxieStartExePath()
    {
        var configuredPath = _settings.SandboxieStartExePath;
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in GetCommonSandboxieStartPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return configuredPath;
    }

    private static IEnumerable<string> GetCommonSandboxieStartPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Sandboxie-Plus", "Start.exe");
            yield return Path.Combine(programFiles, "Sandboxie", "Start.exe");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Sandboxie-Plus", "Start.exe");
            yield return Path.Combine(programFilesX86, "Sandboxie", "Start.exe");
        }
    }

    private static string CreateSessionRoot(string token)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "cloudliteide", token);
        }

        return Path.Combine(Path.GetTempPath(), "cloudliteide", token);
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
            if (_provisioned)
            {
                var iniPath = ResolveSandboxieIniPath();
                if (iniPath is not null)
                {
                    var lines = File.ReadAllLines(iniPath);
                    var updatedLines = RemoveSection(lines, _boxName);
                    File.WriteAllLines(iniPath, updatedLines, Encoding.Unicode);
                    ReloadSandboxieConfigAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
            }

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

    private static List<string> RemoveSection(IReadOnlyList<string> lines, string sectionName)
    {
        var sectionHeader = $"[{sectionName}]";
        var result = new List<string>();
        var inSection = false;

        foreach (var line in lines)
        {
            if (!inSection)
            {
                if (string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                }
                else
                {
                    result.Add(line);
                }

                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.Trim().EndsWith("]", StringComparison.Ordinal))
            {
                inSection = false;
                result.Add(line);
            }
        }

        return result;
    }
}
