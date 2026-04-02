using Doctors.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Doctors.Infrastructure.Data;

/// <summary>
/// Design-time factory for EF Core tools (migrations, etc.)
/// Uses InMemory provider so migrations generate schema without a real connection.
/// </summary>
public class DoctorDbContextFactory : IDesignTimeDbContextFactory<DoctorDbContext>
{
    public DoctorDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DoctorDbContext>();
        optionsBuilder.UseSqlServer("Server=.;Database=DoctorsDesign;Trusted_Connection=True;TrustServerCertificate=True");
        return new DoctorDbContext(optionsBuilder.Options);
    }
}
