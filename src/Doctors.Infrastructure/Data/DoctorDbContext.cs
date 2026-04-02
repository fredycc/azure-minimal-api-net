using Doctors.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Doctors.Infrastructure.Data;

/// <summary>
/// EF Core database context for doctor persistence.
/// </summary>
public class DoctorDbContext(DbContextOptions<DoctorDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Doctor entities table.
    /// </summary>
    public DbSet<Doctor> Doctors => Set<Doctor>();

    /// <summary>
    /// Configures the entity model.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DoctorDbContext).Assembly);
    }
}
