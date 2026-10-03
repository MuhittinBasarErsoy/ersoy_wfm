using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wfm.Application;
using Wfm.Infrastructure.Data;
using Wfm.Infrastructure.Files;
using Wfm.Infrastructure.Identity;

namespace Wfm.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString, string uploadRoot)
    {
        services.AddDbContext<WfmDbContext>(o => o.UseSqlServer(connectionString));

        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 6;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 10;
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<WfmDbContext>();

        services.AddSingleton<IFileStorage>(new LocalFileStorage(uploadRoot));
        return services;
    }
}
