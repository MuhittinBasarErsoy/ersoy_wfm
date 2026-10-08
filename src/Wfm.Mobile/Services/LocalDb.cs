using SQLite;

namespace Wfm.Mobile.Services;

/// <summary>Cihazdaki SQLite veritabanı: görev önbelleği, gönderilmeyi bekleyen işlemler (outbox) ve konum kuyruğu.</summary>
public class LocalDb
{
    private readonly SQLiteAsyncConnection _db;
    private readonly Lazy<Task> _init;

    public LocalDb()
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, "wfm.db3");
        _db = new SQLiteAsyncConnection(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
        _init = new Lazy<Task>(async () =>
        {
            await _db.CreateTableAsync<CachedTask>();
            await _db.CreateTableAsync<CachedType>();
            await _db.CreateTableAsync<OutboxItem>();
            await _db.CreateTableAsync<KeyValue>();
            await _db.CreateTableAsync<PendingPing>();
        });
    }

    public async Task<SQLiteAsyncConnection> Db()
    {
        await _init.Value;
        return _db;
    }

    public async Task<string?> GetValueAsync(string key) => (await (await Db()).FindAsync<KeyValue>(key))?.Value;

    public async Task SetValueAsync(string key, string? value)
    {
        var db = await Db();
        if (value is null) await db.DeleteAsync<KeyValue>(key);
        else await db.InsertOrReplaceAsync(new KeyValue { Key = key, Value = value });
    }

    public async Task ClearAsync()
    {
        var db = await Db();
        await db.DeleteAllAsync<CachedTask>();
        await db.DeleteAllAsync<CachedType>();
        await db.DeleteAllAsync<OutboxItem>();
        await db.DeleteAllAsync<KeyValue>();
        await db.DeleteAllAsync<PendingPing>();
        var dir = OutboxFileDirectory;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    public static string OutboxFileDirectory => Path.Combine(FileSystem.AppDataDirectory, "outbox");
}

public class CachedTask
{
    [PrimaryKey] public Guid Id { get; set; }
    public string Json { get; set; } = "";
}

public class CachedType
{
    [PrimaryKey] public Guid Id { get; set; }
    public string Json { get; set; } = "";
}

public class KeyValue
{
    [PrimaryKey] public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class PendingPing
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Json { get; set; } = "";
}

/// <summary>Sunucuya gönderilmeyi bekleyen işlem. Sırayla (Id'ye göre) gönderilir.</summary>
public class OutboxItem
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Kind { get; set; } = "";
    public Guid TaskId { get; set; }
    public string Payload { get; set; } = "";
    public string? FilePath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public static class OutboxKind
{
    public const string Status = "status";
    public const string Stage = "stage";
    public const string Attachment = "attachment";
    public const string DeleteAttachment = "delete-attachment";
    public const string Shift = "shift";
}
