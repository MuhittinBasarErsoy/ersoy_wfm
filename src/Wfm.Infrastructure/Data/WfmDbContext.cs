using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wfm.Application;
using Wfm.Domain.Common;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Identity;

namespace Wfm.Infrastructure.Data;

public class WfmDbContext(DbContextOptions<WfmDbContext> options, ITenantContext tenant)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TaskType> TaskTypes => Set<TaskType>();
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<TaskEvent> TaskEvents => Set<TaskEvent>();
    public DbSet<TaskAttachment> Attachments => Set<TaskAttachment>();
    public DbSet<LocationPing> LocationPings => Set<LocationPing>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<WorkShift> Shifts => Set<WorkShift>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobTemplate> JobTemplates => Set<JobTemplate>();

    // Global filtre bu alanlara bakar; EF her sorguda güncel değeri kullanır.
    private Guid? CurrentTenantId => tenant.TenantId;
    private bool BypassTenantFilter => tenant.IsSuperAdmin;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(200);
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<Tenant>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(100);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.BrandColor).HasMaxLength(20);
            e.Property(x => x.LogoUrl).HasMaxLength(500);
            e.Property(x => x.LogoPath).HasMaxLength(300);
            e.Property(x => x.LogoContentType).HasMaxLength(50);
        });

        b.Entity<SavedFilter>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Query).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.UserId });
        });

        b.Entity<Team>(e => e.Property(x => x.Name).HasMaxLength(200));

        b.Entity<TaskType>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Icon).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(20);
            e.Property(x => x.Fields).HasConversion(JsonConverter<List<FieldDefinition>>(), JsonComparer<List<FieldDefinition>>());
            e.Property(x => x.Completion).HasConversion(JsonConverter<CompletionRequirements>(), JsonComparer<CompletionRequirements>());
            e.Property(x => x.Stages).HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
        });

        b.Entity<WorkTask>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.CustomerName).HasMaxLength(200);
            e.Property(x => x.CustomerPhone).HasMaxLength(50);
            e.Property(x => x.Stage).HasMaxLength(100);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Property(x => x.CustomFieldValues)
                .HasConversion(JsonConverter<Dictionary<string, string?>>(), JsonComparer<Dictionary<string, string?>>());
            e.HasOne(x => x.TaskType).WithMany().HasForeignKey(x => x.TaskTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.WorkTaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Attachments).WithOne().HasForeignKey(x => x.WorkTaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.AssigneeId, x.UpdatedAt });
            e.Property(x => x.TrackingToken).HasMaxLength(64);
            e.HasIndex(x => x.TrackingToken).IsUnique().HasFilter("[TrackingToken] IS NOT NULL");
            e.HasIndex(x => new { x.JobId, x.StepOrder });
        });

        b.Entity<Job>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.CustomerName).HasMaxLength(200);
            e.Property(x => x.CustomerPhone).HasMaxLength(50);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.Status });
        });

        b.Entity<JobTemplate>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Steps).HasConversion(JsonConverter<List<JobTemplateStep>>(), JsonComparer<List<JobTemplateStep>>());
        });

        b.Entity<TaskEvent>(e => e.Property(x => x.Stage).HasMaxLength(100));

        b.Entity<TaskComment>(e =>
        {
            e.Property(x => x.Body).HasMaxLength(2000);
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.WorkTaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkTaskId, x.CreatedAt });
        });

        b.Entity<TaskAttachment>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.HasIndex(x => x.ClientId);
        });

        b.Entity<LocationPing>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.RecordedAt });
            e.HasIndex(x => new { x.TenantId, x.RecordedAt });
        });

        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Kind).HasMaxLength(30);
        });
        b.Entity<WorkShift>(e => e.HasIndex(x => new { x.UserId, x.EndedAt }));
        b.Entity<RefreshToken>(e => e.HasIndex(x => x.TokenHash).IsUnique());

        // Id'ler uygulamada üretilir; yeni nesneler navigation üzerinden eklendiğinde de Added sayılsın.
        foreach (var et in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
            b.Entity(et.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();

        // Kiracıya ait tüm entity'lere global filtre.
        foreach (var et in b.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            var method = typeof(WfmDbContext).GetMethod(nameof(ApplyTenantFilter),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(et.ClrType);
            method.Invoke(this, [b]);
        }
    }

    /// <summary>SQL Server DateTime'ı Kind'sız döner; tüm tarihleri UTC olarak işaretle ki istemciler doğru çevirsin.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
        c.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantOwned
    {
        Expression<Func<T, bool>> filter = e => BypassTenantFilter || e.TenantId == CurrentTenantId;
        b.Entity<T>().HasQueryFilter(filter);
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is ITenantOwned owned && entry.State == EntityState.Added && owned.TenantId == Guid.Empty)
                owned.TenantId = tenant.TenantId ?? throw new InvalidOperationException("Kiracı bilgisi olmadan kayıt eklenemez.");
            if (entry.Entity is Entity ent && entry.State == EntityState.Modified)
                ent.UpdatedAt = now;
        }
        return base.SaveChangesAsync(ct);
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, Json), v => JsonSerializer.Deserialize<T>(v, Json) ?? new T());

    private static ValueComparer<T> JsonComparer<T>() where T : new() =>
        new((a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
            v => JsonSerializer.Serialize(v, Json).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!);
}
