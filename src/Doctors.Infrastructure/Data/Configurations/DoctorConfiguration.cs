using Doctors.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Doctors.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core entity configuration for the Doctor entity.
/// </summary>
public class DoctorConfiguration : IEntityTypeConfiguration<Doctor>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Doctor> builder)
    {
        builder.ToTable("Doctors");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.FirstName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(d => d.LastName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(d => d.LicenseNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(d => d.Specialty)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(d => d.Email)
            .HasMaxLength(256);

        builder.Property(d => d.Phone)
            .HasMaxLength(20);

        // Unique index on LicenseNumber
        builder.HasIndex(d => d.LicenseNumber)
            .IsUnique();

        // Soft-delete global query filter
        builder.HasQueryFilter(d => d.IsActive);
    }
}
