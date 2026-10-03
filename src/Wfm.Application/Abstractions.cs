namespace Wfm.Application;

/// <summary>İstek bazında aktif kullanıcı ve kiracı bilgisi.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? UserId { get; }
    bool IsSuperAdmin { get; }
}

/// <summary>Dosya depolama soyutlaması (yerel disk, sonradan Azure Blob vb.).</summary>
public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string relativePath, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct = default);
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
}
