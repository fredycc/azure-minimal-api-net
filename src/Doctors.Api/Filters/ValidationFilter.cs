using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Doctors.Api.Filters;

/// <summary>
/// Endpoint filter that validates required fields on request body arguments.
/// </summary>
public class ValidationFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var requestArguments = context.Arguments;

        foreach (var argument in requestArguments)
        {
            if (argument is null) continue;

            var validationContext = new ValidationContext(argument);
            var validationResults = new List<ValidationResult>();

            if (!Validator.TryValidateObject(argument, validationContext, validationResults, validateAllProperties: true))
            {
                var errors = validationResults
                    .Where(r => r.ErrorMessage is not null)
                    .Select(r => r.ErrorMessage!)
                    .ToList();

                return Results.ValidationProblem(
                    errors.ToDictionary(e => "validation", e => new[] { e }),
                    title: "Validation Failed",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return await next(context);
    }
}
