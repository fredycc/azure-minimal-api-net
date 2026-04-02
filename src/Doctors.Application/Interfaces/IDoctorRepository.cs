using Doctors.Domain.Entities;

namespace Doctors.Application.Interfaces;

/// <summary>
/// Repository contract for doctor persistence operations.
/// </summary>
public interface IDoctorRepository
{
    /// <summary>
    /// Retrieves all active doctors.
    /// </summary>
    Task<IReadOnlyList<Doctor>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves a doctor by their unique identifier.
    /// </summary>
    /// <returns>The doctor, or null if not found or inactive.</returns>
    Task<Doctor?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Checks if a doctor with the given license number already exists.
    /// </summary>
    /// <param name="licenseNumber">License number to check.</param>
    /// <param name="excludeId">Optional doctor ID to exclude from the check (for updates).</param>
    /// <returns>True if a doctor with that license number exists.</returns>
    Task<bool> ExistsByLicenseNumberAsync(string licenseNumber, Guid? excludeId = null, CancellationToken ct = default);

    /// <summary>
    /// Adds a new doctor to the data store.
    /// </summary>
    /// <returns>The persisted doctor entity.</returns>
    Task<Doctor> AddAsync(Doctor doctor, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing doctor in the data store.
    /// </summary>
    Task UpdateAsync(Doctor doctor, CancellationToken ct = default);
}
