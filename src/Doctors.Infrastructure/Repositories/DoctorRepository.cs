using Doctors.Application.Interfaces;
using Doctors.Domain.Entities;
using Doctors.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Doctors.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of the doctor repository.
/// </summary>
public class DoctorRepository(DoctorDbContext context) : IDoctorRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Doctor>> GetAllAsync(CancellationToken ct = default)
    {
        return [.. await context.Doctors.ToListAsync(ct)];
    }

    /// <inheritdoc />
    public async Task<Doctor?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await context.Doctors.FindAsync([id], ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByLicenseNumberAsync(string licenseNumber, Guid? excludeId = null, CancellationToken ct = default)
    {
        var query = context.Doctors.IgnoreQueryFilters().Where(d => d.LicenseNumber == licenseNumber);

        if (excludeId.HasValue)
        {
            query = query.Where(d => d.Id != excludeId.Value);
        }

        return await query.AnyAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Doctor> AddAsync(Doctor doctor, CancellationToken ct = default)
    {
        context.Doctors.Add(doctor);
        await context.SaveChangesAsync(ct);
        return doctor;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Doctor doctor, CancellationToken ct = default)
    {
        context.Doctors.Update(doctor);
        await context.SaveChangesAsync(ct);
    }
}
