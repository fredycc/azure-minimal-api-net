using System.Diagnostics;
using Doctors.Api.Filters;
using Doctors.Application.DTOs;
using Doctors.Application.Services;
using Doctors.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Doctors.Api.Endpoints;

/// <summary>
/// Extension methods for mapping doctor CRUD endpoints.
/// </summary>
public static class DoctorEndpoints
{
    /// <summary>
    /// Maps all doctor-related endpoints to the application.
    /// </summary>
    public static RouteGroupBuilder MapDoctorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/doctors")
            .WithTags("Doctors")
            .AddEndpointFilter<LoggingFilter>();

        group.MapGet("/", GetAllDoctorsAsync)
            .WithSummary("Get all doctors")
            .WithDescription("Returns a list of all active specialist doctors.");

        group.MapGet("/{id:guid}", GetDoctorByIdAsync)
            .WithSummary("Get doctor by ID")
            .WithDescription("Returns a single specialist doctor by their unique identifier.");

        group.MapPost("/", CreateDoctorAsync)
            .AddEndpointFilter<ValidationFilter>()
            .RequireAuthorization()
            .WithSummary("Create a new doctor")
            .WithDescription("Creates a new specialist doctor record.")
            .Produces(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", UpdateDoctorAsync)
            .AddEndpointFilter<ValidationFilter>()
            .RequireAuthorization()
            .WithSummary("Update an existing doctor")
            .WithDescription("Updates the details of an existing specialist doctor.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", DeleteDoctorAsync)
            .RequireAuthorization()
            .WithSummary("Delete a doctor")
            .WithDescription("Soft-deletes a specialist doctor by setting IsActive to false.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetAllDoctorsAsync(
        IDoctorService service, CancellationToken ct)
    {
        var doctors = await service.GetAllAsync(ct);
        return Results.Ok(doctors);
    }

    private static async Task<IResult> GetDoctorByIdAsync(
        Guid id, IDoctorService service, CancellationToken ct)
    {
        var doctor = await service.GetByIdAsync(id, ct);
        return Results.Ok(doctor);
    }

    private static async Task<IResult> CreateDoctorAsync(
        CreateDoctorRequest request, IDoctorService service, HttpContext httpContext, CancellationToken ct)
    {
        var doctor = await service.CreateAsync(request, ct);
        return Results.Created($"/api/doctors/{doctor.Id}", doctor);
    }

    private static async Task<IResult> UpdateDoctorAsync(
        Guid id, UpdateDoctorRequest request, IDoctorService service, CancellationToken ct)
    {
        var doctor = await service.UpdateAsync(id, request, ct);
        return Results.Ok(doctor);
    }

    private static async Task<IResult> DeleteDoctorAsync(
        Guid id, IDoctorService service, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Results.NoContent();
    }
}
