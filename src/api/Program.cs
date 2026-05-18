using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLiteIDE.Agents;
using CloudLiteIDE.Api.Models;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<ExecutorSettings>()
    .Bind(builder.Configuration.GetSection(ExecutorSettings.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExecutorSettings>>().Value);
builder.Services.AddSingleton<ExecutorRegistry>();
builder.Services.AddHostedService<ExecutorCleanupHostedService>();

var app = builder.Build();

var frontendRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "frontend"));
if (Directory.Exists(frontendRoot))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(frontendRoot)
    });

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(frontendRoot)
    });
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() }
};

app.MapPost("/api/executors", (ExecutorRegistry registry) =>
{
    var (executorId, leaseTtlSeconds) = registry.Create();
    return Results.Ok(new CreateExecutorResponse(executorId, leaseTtlSeconds));
});

app.MapPost("/api/executors/{executorId:guid}/renew", (Guid executorId, ExecutorRegistry registry) =>
    registry.Renew(executorId)
        ? Results.NoContent()
        : Results.Problem("Executor lease expired or was not found.", statusCode: StatusCodes.Status410Gone));

app.MapDelete("/api/executors/{executorId:guid}", (Guid executorId, ExecutorRegistry registry) =>
{
    registry.Release(executorId);
    return Results.NoContent();
});

app.MapGet("/api/executors/{executorId:guid}/events", async (Guid executorId, ExecutorRegistry registry, HttpContext context) =>
{
    if (!registry.TrySubscribe(executorId, out var reader) || reader is null)
    {
        context.Response.StatusCode = StatusCodes.Status410Gone;
        await context.Response.WriteAsJsonAsync(new ErrorResponse("Executor lease expired or was not found."));
        return;
    }

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    await foreach (var evt in reader.ReadAllAsync(context.RequestAborted))
    {
        var payload = JsonSerializer.Serialize(evt, jsonOptions);
        await context.Response.WriteAsync($"event: {evt.Type}\n", context.RequestAborted);
        await context.Response.WriteAsync($"data: {payload}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});

app.MapPost("/api/executors/{executorId:guid}/execute", async (Guid executorId, ExecuteRequest request, ExecutorRegistry registry, HttpContext httpContext) =>
{
    var validation = ExecuteRequestValidator.Validate(request);
    if (!validation.IsValid)
    {
        return Results.BadRequest(new ErrorResponse(validation.ErrorMessage!));
    }

    var result = await registry.ExecuteAsync(executorId, request, httpContext.RequestAborted);
    return result is null
        ? Results.Problem("Executor lease expired or was not found.", statusCode: StatusCodes.Status410Gone)
        : Results.Accepted();
});

app.Run();

public partial class Program;
