using System.Text.RegularExpressions;

namespace Doctors.Domain.Entities;

/// <summary>
/// Represents a specialist doctor in the system.
/// </summary>
public sealed class Doctor
{
    /// <summary>
    /// Unique identifier for the doctor.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Doctor's first name. Required, max 100 characters.
    /// </summary>
    public required string FirstName { get; set; }

    /// <summary>
    /// Doctor's last name. Required, max 100 characters.
    /// </summary>
    public required string LastName { get; set; }

    /// <summary>
    /// Doctor's medical license number. Must match pattern [A-Z0-9-]+. Unique across all doctors.
    /// </summary>
    public required string LicenseNumber
    {
        get => field;
        set
        {
            if (!Regex.IsMatch(value, @"^[A-Z0-9-]+$"))
            {
                throw new Exceptions.DomainException(
                    $"LicenseNumber must match pattern [A-Z0-9-]+, got: '{value}'.");
            }
            field = value;
        }
    }

    /// <summary>
    /// Doctor's medical specialty. Required, max 50 characters.
    /// </summary>
    public required string Specialty { get; set; }

    /// <summary>
    /// Doctor's email address. Optional, validated if provided.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Doctor's phone number. Optional.
    /// </summary>
    public string? Phone { get; set; }

    /// <summary>
    /// Indicates if the doctor is active. Set to false on soft-delete.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Timestamp when the doctor was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the doctor was last updated (UTC).
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
