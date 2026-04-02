using Doctors.Application.DTOs;
using Doctors.Application.Interfaces;
using Doctors.Application.Mappings;
using Doctors.Domain.Entities;
using Doctors.Domain.Exceptions;

namespace Doctors.Application.Services;

/// <summary>
/// Service orchestrating doctor CRUD use cases.
/// </summary>
public class DoctorService(IDoctorRepository repository) : IDoctorService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DoctorDto>> GetAllAsync(CancellationToken ct = default)
    {
        var doctors = await repository.GetAllAsync(ct);
        return [.. doctors.Select(d => d.ToDto())];
    }

    /// <inheritdoc />
    public async Task<DoctorDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var doctor = await repository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(Doctor), id);
        return doctor.ToDto();
    }

    /// <inheritdoc />
    public async Task<DoctorDto> CreateAsync(CreateDoctorRequest request, CancellationToken ct = default)
    {
        if (await repository.ExistsByLicenseNumberAsync(request.LicenseNumber, ct: ct))
        {
            throw new ConflictException($"A doctor with license number '{request.LicenseNumber}' already exists.");
        }

        var doctor = request.ToEntity();
        var created = await repository.AddAsync(doctor, ct);
        return created.ToDto();
    }

    /// <inheritdoc />
    public async Task<DoctorDto> UpdateAsync(Guid id, UpdateDoctorRequest request, CancellationToken ct = default)
    {
        var doctor = await repository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(Doctor), id);

        if (await repository.ExistsByLicenseNumberAsync(request.LicenseNumber, id, ct))
        {
            throw new ConflictException($"A doctor with license number '{request.LicenseNumber}' already exists.");
        }

        request.ApplyTo(doctor);
        await repository.UpdateAsync(doctor, ct);
        return doctor.ToDto();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var doctor = await repository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(Doctor), id);

        doctor.IsActive = false;
        doctor.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(doctor, ct);
    }
}
