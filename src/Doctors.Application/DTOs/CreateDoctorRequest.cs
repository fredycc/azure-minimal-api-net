using System.ComponentModel.DataAnnotations;

namespace Doctors.Application.DTOs;

/// <summary>
/// Request to create a new doctor.
/// </summary>
public record CreateDoctorRequest
{
    /// <summary>
    /// Doctor's first name. Required, max 100 characters.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public required string FirstName { get; init; }

    /// <summary>
    /// Doctor's last name. Required, max 100 characters.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public required string LastName { get; init; }

    /// <summary>
    /// Medical license number. Required, must match pattern [A-Z0-9-]+.
    /// </summary>
    [Required]
    [RegularExpression(@"^[A-Z0-9-]+$", ErrorMessage = "LicenseNumber must contain only uppercase letters, digits, and hyphens.")]
    public required string LicenseNumber { get; init; }

    /// <summary>
    /// Medical specialty. Required, max 50 characters.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public required string Specialty { get; init; }

    /// <summary>
    /// Email address. Optional, validated if provided.
    /// </summary>
    [EmailAddress]
    public string? Email { get; init; }

    /// <summary>
    /// Phone number. Optional.
    /// </summary>
    public string? Phone { get; init; }
}
