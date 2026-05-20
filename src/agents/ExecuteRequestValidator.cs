namespace CloudLiteIDE.Agents;

public static class ExecuteRequestValidator
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "cpp",
        "python"
    };

    private static readonly HashSet<string> SupportedCompilers = new(StringComparer.OrdinalIgnoreCase)
    {
        "g++",
        "clang++"
    };

    private static readonly HashSet<string> SupportedStandards = new(StringComparer.OrdinalIgnoreCase)
    {
        "c++11",
        "c++14",
        "c++17",
        "c++20",
        "c++23"
    };

    private static readonly HashSet<string> SupportedOptimizations = new(StringComparer.OrdinalIgnoreCase)
    {
        "O0",
        "O1",
        "O2",
        "O3",
        "Ofast"
    };

    private static readonly HashSet<string> SupportedWarnings = new(StringComparer.OrdinalIgnoreCase)
    {
        "none",
        "Wall",
        "Wextra",
        "Wpedantic"
    };

    public static ExecuteValidationResult Validate(ExecuteRequest? request)
    {
        if (request is null)
        {
            return ExecuteValidationResult.Fail("Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Language) || !SupportedLanguages.Contains(request.Language))
        {
            return ExecuteValidationResult.Fail("Unsupported language. Supported values: cpp, python.");
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return ExecuteValidationResult.Fail("Code is required.");
        }

        if (request.Code.Length > 1_000_000)
        {
            return ExecuteValidationResult.Fail("Code size exceeds 1,000,000 characters limit.");
        }

        if (request.Language.Equals("cpp", StringComparison.OrdinalIgnoreCase))
        {
            var options = request.CppOptions ?? new CompilerOptions();

            if (!SupportedCompilers.Contains(options.Compiler))
            {
                return ExecuteValidationResult.Fail("Unsupported C++ compiler. Supported values: g++, clang++.");
            }

            if (!SupportedStandards.Contains(options.Standard))
            {
                return ExecuteValidationResult.Fail("Unsupported C++ standard.");
            }

            if (!SupportedOptimizations.Contains(options.Optimization))
            {
                return ExecuteValidationResult.Fail("Unsupported optimization level.");
            }

            if (!SupportedWarnings.Contains(options.WarningLevel))
            {
                return ExecuteValidationResult.Fail("Unsupported warning level.");
            }
        }

        return ExecuteValidationResult.Success();
    }
}
