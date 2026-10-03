namespace Wfm.Domain.Common;

/// <summary>Bir kiracıya (şirkete) ait olan tüm entity'ler bunu uygular; EF global filtre buna göre çalışır.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public abstract class TenantEntity : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
}

public class DomainException(string message) : Exception(message);
