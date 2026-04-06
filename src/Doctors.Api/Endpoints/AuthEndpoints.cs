using Doctors.Api.DTOs;
using Doctors.Api.Services;

namespace Doctors.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/auth")
            .WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .WithSummary("Authenticate and get JWT token")
            .WithDescription("Validates credentials and returns a signed JWT token valid for 60 minutes.")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return group;
    }

    private static IResult LoginAsync(
        LoginRequest request,
        TokenService tokenService)
    {
        var response = tokenService.GenerateToken(request);
        return response is null
            ? Results.Unauthorized()
            : Results.Ok(response);
    }
}