using Doctors.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Doctors.Application;

/// <summary>
/// Dependency injection extensions for the Application layer.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Application layer services.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IDoctorService, DoctorService>();
        return services;
    }
}
