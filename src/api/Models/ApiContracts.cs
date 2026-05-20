namespace CloudLiteIDE.Api.Models;

public sealed record CreateExecutorResponse(Guid ExecutorId, int LeaseTtlSeconds);
public sealed record ErrorResponse(string Error);
