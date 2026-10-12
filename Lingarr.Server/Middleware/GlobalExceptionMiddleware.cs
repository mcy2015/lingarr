using System.Text.Json;

namespace Lingarr.Server.Middleware;

/// <summary>
/// Terminal exception handler for unhandled controller/hub failures: logs the
/// exception with a correlation id and returns a single, non-leaking 500 body
/// shape (<c>{ message, traceId }</c>) instead of raw exception messages.
/// Domain-specific error mapping (validation, auth, translation failures)
/// stays with the individual endpoints.
/// </summary>
public class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — nothing to report back to.
            throw;
        }
        catch (Exception ex)
        {
            var traceId = context.TraceIdentifier;
            _logger.LogError(ex, "Unhandled exception for {Method} {Path} (trace {TraceId})",
                context.Request.Method, context.Request.Path, traceId);

            if (context.Response.HasStarted)
            {
                // Too late to replace the response (e.g. mid-SSE) — abort the connection.
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { message = "An unexpected error occurred.", traceId }, JsonOptions));
        }
    }
}
