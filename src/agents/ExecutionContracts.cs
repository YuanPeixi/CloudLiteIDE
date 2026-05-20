using System.Text.Json.Serialization;

namespace CloudLiteIDE.Agents;

public enum ExecutorMode
{
    Native,
    Sandboxie
}

public enum StreamEventType
{
    Status,
    CompileStdout,
    CompileStderr,
    RuntimeStdout,
    RuntimeStderr,
    Error,
    Result
}

public sealed record CompilerOptions(
    string Compiler = "g++",
    string Standard = "c++17",
    string Optimization = "O2",
    string WarningLevel = "Wall");

public sealed record ExecuteRequest(
    string Language,
    string Code,
    string? Stdin,
    CompilerOptions? CppOptions);

public sealed record ExecuteValidationResult(bool IsValid, string? ErrorMessage)
{
    public static ExecuteValidationResult Success() => new(true, null);
    public static ExecuteValidationResult Fail(string message) => new(false, message);
}

public sealed record ExecutionEvent(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StreamEventType Type,
    string Message,
    DateTimeOffset Timestamp,
    bool Final = false);

public sealed record ExecutionResult(bool Success, string Message, int? ExitCode = null);

public sealed class ExecutorSettings
{
    public const string SectionName = "Executor";

    public ExecutorMode Mode { get; init; } = ExecutorMode.Native;
    public string SandboxieStartExePath { get; init; } = "Start.exe";
    public string? SandboxieBoxName { get; init; }
    public string SandboxieTemplateBoxName { get; init; } = "PrivacyEnhanced";
    public int LeaseTtlSeconds { get; init; } = 120;
    public int RunTimeoutSeconds { get; init; } = 10;
    public int CompileTimeoutSeconds { get; init; } = 10;
    public int MaxOutputBytes { get; init; } = 1_048_576;
    public int MaxMemoryMb { get; init; } = 256;
    public int MaxCpuPercent { get; init; } = 80;
}
