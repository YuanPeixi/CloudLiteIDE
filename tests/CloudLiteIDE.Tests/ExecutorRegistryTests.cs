using CloudLiteIDE.Agents;

namespace CloudLiteIDE.Tests;

public class ExecutorRegistryTests
{
    [Fact]
    public void Create_ThenRenew_ExtendsLease()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new ExecutorRegistry(new ExecutorSettings { LeaseTtlSeconds = 10 }, () => now);

        var (executorId, _) = registry.Create();

        now = now.AddSeconds(9);
        Assert.True(registry.Renew(executorId));

        now = now.AddSeconds(9);
        Assert.True(registry.Renew(executorId));
    }

    [Fact]
    public void CleanupExpired_RemovesExpiredExecutors()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new ExecutorRegistry(new ExecutorSettings { LeaseTtlSeconds = 5 }, () => now);

        var (executorId, _) = registry.Create();

        now = now.AddSeconds(6);

        var removed = registry.CleanupExpired();

        Assert.Equal(1, removed);
        Assert.False(registry.Renew(executorId));
    }

    [Fact]
    public void ValidateRequest_RejectsInvalidCppCompiler()
    {
        var result = ExecuteRequestValidator.Validate(new ExecuteRequest(
            "cpp",
            "int main(){return 0;}",
            null,
            new CompilerOptions(Compiler: "invalid", Standard: "c++17", Optimization: "O2", WarningLevel: "Wall")));

        Assert.False(result.IsValid);
        Assert.Contains("compiler", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }
}
