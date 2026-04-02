using Doctors.Application.DTOs;
using Doctors.Domain.Entities;

namespace Doctors.Application.Mappings;

/// <summary>
/// Extension methods for mapping between Doctor entities and DTOs.
/// </summary>
public static class DoctorMappingExtensions
{
    /// <summary>
    /// Maps a Doctor entity to a DoctorDto.
    /// </summary>
    public static DoctorDto ToDto(this Doctor source) => new(
        source.Id,
        source.FirstName,
        source.LastName,
        source.LicenseNumber,
        source.Specialty,
        source.Email,
        source.Phone,
        source.IsActive);

    /// <summary>
    /// Maps a CreateDoctorRequest to a Doctor entity.
    /// </summary>
    public static Doctor ToEntity(this CreateDoctorRequest source) => new()
    {
        FirstName = source.FirstName,
        LastName = source.LastName,
        LicenseNumber = source.LicenseNumber,
        Specialty = source.Specialty,
        Email = source.Email,
        Phone = source.Phone
    };

    /// <summary>
    /// Updates an existing Doctor entity from an UpdateDoctorRequest.
    /// </summary>
    public static void ApplyTo(this UpdateDoctorRequest source, Doctor target)
    {
        target.FirstName = source.FirstName;
        target.LastName = source.LastName;
        target.LicenseNumber = source.LicenseNumber;
        target.Specialty = source.Specialty;
        target.Email = source.Email;
        target.Phone = source.Phone;
        target.UpdatedAt = DateTime.UtcNow;
    }
}
