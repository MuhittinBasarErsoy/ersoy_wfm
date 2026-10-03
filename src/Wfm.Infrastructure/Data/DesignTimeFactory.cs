using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wfm.Application;

namespace Wfm.Infrastructure.Data;

/// <summary>`dotnet ef migrations` için; çalışma zamanında kullanılmaz.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<WfmDbContext>
{
    public WfmDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WfmDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=WfmDev;Trusted_Connection=True")
            .Options, new NoTenant());

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
        public Guid? UserId => null;
        public bool IsSuperAdmin => false;
    }
}
