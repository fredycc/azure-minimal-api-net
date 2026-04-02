namespace Doctors.Application.DTOs;

/// <summary>
/// Data transfer object representing a doctor for API responses.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="FirstName">First name.</param>
/// <param name="LastName">Last name.</param>
/// <param name="LicenseNumber">Medical license number.</param>
/// <param name="Specialty">Medical specialty.</param>
/// <param name="Email">Email address.</param>
/// <param name="Phone">Phone number.</param>
/// <param name="IsActive">Whether the doctor is active.</param>
public record DoctorDto(
    Guid Id,
    string FirstName,
    string LastName,
    string LicenseNumber,
    string Specialty,
    string? Email,
    string? Phone,
    bool IsActive);
