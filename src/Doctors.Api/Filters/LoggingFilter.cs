using System.Diagnostics;

namespace Doctors.Api.Filters;

/// <summary>
/// Endpoint filter that logs HTTP method, path, and elapsed duration.
/// </summary>
public class LoggingFilter(ILogger<LoggingFilter> logger) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var method = httpContext.Request.Method;
        var path = httpContext.Request.Path;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await next(context);
            stopwatch.Stop();

            logger.LogInformation(
                "{Method} {Path} responded in {ElapsedMs}ms",
                method, path, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            logger.LogInformation(
                "{Method} {Path} responded in {ElapsedMs}ms",
                method, path, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
