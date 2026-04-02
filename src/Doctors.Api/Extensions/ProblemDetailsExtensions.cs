using System.Diagnostics;
using Doctors.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Doctors.Api.Extensions;

/// <summary>
/// Extension methods for mapping domain exceptions to ProblemDetails responses.
/// </summary>
public static class ProblemDetailsExtensions
{
    /// <summary>
    /// Registers global exception handler middleware that maps domain exceptions to HTTP ProblemDetails.
    /// </summary>
    public static WebApplication UseProblemDetails(this WebApplication app)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                var exception = exceptionFeature?.Error;

                var (statusCode, title) = exception switch
                {
                    NotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
                    ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
                    _ => (StatusCodes.Status500InternalServerError, "Internal Server Error")
                };

                var problemDetails = new ProblemDetails
                {
                    Status = statusCode,
                    Title = title,
                    Detail = exception?.Message,
                    Instance = context.Request.Path
                };

                problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;

                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(problemDetails);
            });
        });

        return app;
    }
}
