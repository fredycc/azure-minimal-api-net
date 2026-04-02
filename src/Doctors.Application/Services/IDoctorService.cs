using Doctors.Application.DTOs;

namespace Doctors.Application.Services;

/// <summary>
/// Service interface for doctor CRUD operations.
/// </summary>
public interface IDoctorService
{
    /// <summary>
    /// Retrieves all active doctors.
    /// </summary>
    Task<IReadOnlyList<DoctorDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves a doctor by their unique identifier.
    /// </summary>
    /// <exception cref="Doctors.Domain.Exceptions.NotFoundException">Thrown when doctor not found.</exception>
    Task<DoctorDto> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Creates a new doctor.
    /// </summary>
    /// <exception cref="Doctors.Domain.Exceptions.ConflictException">Thrown when license number already exists.</exception>
    Task<DoctorDto> CreateAsync(CreateDoctorRequest request, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing doctor.
    /// </summary>
    /// <exception cref="Doctors.Domain.Exceptions.NotFoundException">Thrown when doctor not found.</exception>
    /// <exception cref="Doctors.Domain.Exceptions.ConflictException">Thrown when license number already exists.</exception>
    Task<DoctorDto> UpdateAsync(Guid id, UpdateDoctorRequest request, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a doctor (sets IsActive = false).
    /// </summary>
    /// <exception cref="Doctors.Domain.Exceptions.NotFoundException">Thrown when doctor not found.</exception>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
