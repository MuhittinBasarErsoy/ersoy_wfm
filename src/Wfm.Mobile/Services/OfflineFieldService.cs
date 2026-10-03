using System.Text.Json;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Shared.UI.Services;

namespace Wfm.Mobile.Services;

/// <summary>
/// Offline-öncelikli saha servisi. Görevler SQLite'ta önbelleklenir; durum değişiklikleri ve ekler önce yerelde uygulanıp
/// outbox kuyruğuna yazılır, bağlantı olduğunda sırayla sunucuya gönderilir. Sunucu reddederse (ör. görev bu arada iptal
/// edildi) işlem düşürülür, kullanıcıya hata gösterilir ve sunucu durumu yeniden çekilir.
/// </summary>
public sealed class OfflineFieldService : IFieldService, IDisposable
{
    private readonly LocalDb _local;
    private readonly WfmApiClient _api;
    private readonly AuthSession _session;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Timer _timer;
    private static readonly JsonSerializerOptions Json = WfmJson.Options;

    public event Action? Changed;
    public bool IsOnline { get; private set; } = true;
    public int PendingCount { get; private set; }
    public DateTime? LastSync { get; private set; }
    public string? LastError { get; private set; }

    public OfflineFieldService(LocalDb local, WfmApiClient api, AuthSession session)
    {
        _local = local;
        _api = api;
        _session = session;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        IsOnline = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
        // Ön planda dakikada bir senkronize et (outbox + yeni görevler).
        _timer = new Timer(_ => { if (_session.Current is not null && !AppState.IsInBackground) _ = SyncAsync(); },
            null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // ---------- Okuma ----------
    public async Task<IReadOnlyList<WorkTaskDetailDto>> GetTasksAsync()
    {
        var rows = await (await _local.Db()).Table<CachedTask>().ToListAsync();
        if (rows.Count == 0 && LastSync is null) await SyncAsync();
        rows = await (await _local.Db()).Table<CachedTask>().ToListAsync();
        return rows.Select(r => JsonSerializer.Deserialize<WorkTaskDetailDto>(r.Json, Json)!).ToList();
    }

    public async Task<WorkTaskDetailDto?> GetTaskAsync(Guid id)
    {
        var row = await (await _local.Db()).FindAsync<CachedTask>(id);
        if (row is null && LastSync is null)
        {
            await SyncAsync();
            row = await (await _local.Db()).FindAsync<CachedTask>(id);
        }
        return row is null ? null : JsonSerializer.Deserialize<WorkTaskDetailDto>(row.Json, Json);
    }

    public async Task<TaskTypeDto?> GetTaskTypeAsync(Guid id)
    {
        var row = await (await _local.Db()).FindAsync<CachedType>(id);
        return row is null ? null : JsonSerializer.Deserialize<TaskTypeDto>(row.Json, Json);
    }

    public async Task<ShiftDto?> GetShiftAsync()
    {
        var json = await _local.GetValueAsync("shift");
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ShiftDto>(json, Json);
    }

    // ---------- Yazma (offline destekli) ----------
    public async Task ChangeStatusAsync(Guid taskId, ChangeStatusRequest request)
    {
        // Önce bekleyen işlemleri gönder ki sıra korunsun (ör. offline çekilen fotoğraflar tamamlamadan önce gitmeli).
        await PushOutboxAsync();
        if (PendingCount == 0)
        {
            try
            {
                await _api.ChangeStatusAsync(taskId, request);
                await PullAsync();
                return;
            }
            catch (ApiException ex) when (ex.IsNetworkError)
            {
                SetOnline(false);
            }
        }

        // Çevrimdışı: yerelde uygula ve kuyruğa al.
        await UpdateCachedTaskAsync(taskId, t =>
        {
            var values = new Dictionary<string, string?>(t.CustomFieldValues);
            foreach (var kv in request.CustomFieldValues ?? []) values[kv.Key] = kv.Value;
            var now = request.ClientTimestamp ?? DateTime.UtcNow;
            return t with
            {
                Status = request.Status,
                CustomFieldValues = values,
                CompletionNote = request.Status == WorkTaskStatus.Completed ? request.Note : t.CompletionNote,
                StartedAt = request.Status == WorkTaskStatus.EnRoute ? t.StartedAt ?? now : t.StartedAt,
                CompletedAt = request.Status is WorkTaskStatus.Completed or WorkTaskStatus.Failed ? now : t.CompletedAt,
                UpdatedAt = now,
                Events = [.. t.Events, new TaskEventDto(Guid.NewGuid(), _session.Current?.User.Id ?? Guid.Empty,
                    _session.Current?.User.FullName + " (gönderilmedi)", t.Status, request.Status, request.Note,
                    request.Latitude, request.Longitude, now)]
            };
        });
        await EnqueueAsync(new OutboxItem { Kind = OutboxKind.Status, TaskId = taskId, Payload = JsonSerializer.Serialize(request, Json) });
    }

    public async Task AddAttachmentAsync(Guid taskId, CapturedFile file, AttachmentKind kind, GeoPoint? location)
    {
        var clientId = Guid.NewGuid();
        Directory.CreateDirectory(LocalDb.OutboxFileDirectory);
        var path = Path.Combine(LocalDb.OutboxFileDirectory, $"{clientId:N}{Path.GetExtension(file.FileName)}");
        await File.WriteAllBytesAsync(path, file.Data);

        // Yerelde hemen göster (data: URL), arka planda yükle.
        var local = new AttachmentDto(clientId, kind, file.FileName, file.ContentType,
            $"data:{file.ContentType};base64,{Convert.ToBase64String(file.Data)}",
            location?.Latitude, location?.Longitude, DateTime.UtcNow, _session.Current?.User.Id ?? Guid.Empty);
        await UpdateCachedTaskAsync(taskId, t => t with { Attachments = [.. t.Attachments, local] });

        var meta = new AttachmentMeta(clientId, kind, file.FileName, file.ContentType, location?.Latitude, location?.Longitude, DateTime.UtcNow);
        await EnqueueAsync(new OutboxItem
        {
            Kind = OutboxKind.Attachment, TaskId = taskId, Payload = JsonSerializer.Serialize(meta, Json), FilePath = path
        });
    }

    public async Task RemoveAttachmentAsync(Guid taskId, Guid attachmentId)
    {
        var db = await _local.Db();
        // Henüz gönderilmemiş bir ekse kuyruktan çıkar.
        var pending = (await db.Table<OutboxItem>().Where(o => o.Kind == OutboxKind.Attachment && o.TaskId == taskId).ToListAsync())
            .FirstOrDefault(o => JsonSerializer.Deserialize<AttachmentMeta>(o.Payload, Json)!.ClientId == attachmentId);
        if (pending is not null)
        {
            await db.DeleteAsync(pending);
            if (pending.FilePath is not null && File.Exists(pending.FilePath)) File.Delete(pending.FilePath);
        }
        else
        {
            await EnqueueAsync(new OutboxItem { Kind = OutboxKind.DeleteAttachment, TaskId = taskId, Payload = attachmentId.ToString() }, push: false);
        }
        await UpdateCachedTaskAsync(taskId, t => t with { Attachments = t.Attachments.Where(a => a.Id != attachmentId).ToList() });
        await RefreshPendingCountAsync();
        await PushOutboxAsync();
    }

    public async Task<ShiftDto?> SetShiftAsync(bool onShift)
    {
        ShiftDto? shift = onShift ? new ShiftDto(Guid.NewGuid(), DateTime.UtcNow, null) : null;
        try
        {
            if (onShift) shift = await _api.StartShiftAsync();
            else await _api.EndShiftAsync();
        }
        catch (ApiException ex) when (ex.IsNetworkError)
        {
            SetOnline(false);
            await EnqueueAsync(new OutboxItem { Kind = OutboxKind.Shift, Payload = onShift ? "start" : "end" }, push: false);
        }
        await _local.SetValueAsync("shift", shift is null ? null : JsonSerializer.Serialize(shift, Json));
        Changed?.Invoke();
        return shift;
    }

    public async Task OnServerTaskChangedAsync(WorkTaskDto task) => await SyncAsync();

    public async Task ClearAsync()
    {
        await _local.ClearAsync();
        PendingCount = 0;
        LastSync = null;
        LastError = null;
        Changed?.Invoke();
    }

    // ---------- Senkronizasyon ----------
    public async Task SyncAsync()
    {
        if (_session.Current is null) return;
        await PushOutboxAsync();
        if (IsOnline || Connectivity.Current.NetworkAccess == NetworkAccess.Internet) await PullAsync();
        await LocationPipeline.FlushPendingAsync(_local, _api);
    }

    /// <summary>Kuyruktaki işlemleri sırayla gönderir. Ağ hatasında durur, sunucu reddinde işlemi düşürür.</summary>
    private async Task PushOutboxAsync()
    {
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(30))) return;
        try
        {
            var db = await _local.Db();
            var items = await db.Table<OutboxItem>().OrderBy(o => o.Id).ToListAsync();
            foreach (var item in items)
            {
                try
                {
                    await SendAsync(item);
                    await db.DeleteAsync(item);
                    if (item.FilePath is not null && File.Exists(item.FilePath)) File.Delete(item.FilePath);
                    SetOnline(true);
                }
                catch (ApiException ex) when (ex.IsNetworkError || (int?)ex.Status >= 500 || ex.Status == System.Net.HttpStatusCode.Unauthorized)
                {
                    // Geçici hata: sonra tekrar dene, sırayı bozma.
                    item.Attempts++;
                    item.LastError = ex.Message;
                    await db.UpdateAsync(item);
                    if (ex.IsNetworkError) SetOnline(false);
                    break;
                }
                catch (ApiException ex)
                {
                    // Kalıcı ret (400/403/404/409): işlemi düşür, kullanıcıyı bilgilendir.
                    LastError = $"Gönderilemeyen işlem atlandı: {ex.Message}";
                    await db.DeleteAsync(item);
                    if (item.FilePath is not null && File.Exists(item.FilePath)) File.Delete(item.FilePath);
                }
            }
            await RefreshPendingCountAsync();
        }
        finally
        {
            _syncLock.Release();
            Changed?.Invoke();
        }
    }

    private async Task SendAsync(OutboxItem item)
    {
        switch (item.Kind)
        {
            case OutboxKind.Status:
                await _api.ChangeStatusAsync(item.TaskId, JsonSerializer.Deserialize<ChangeStatusRequest>(item.Payload, Json)!);
                break;
            case OutboxKind.Attachment:
                var meta = JsonSerializer.Deserialize<AttachmentMeta>(item.Payload, Json)!;
                if (item.FilePath is null || !File.Exists(item.FilePath)) return; // dosya silinmiş: atla
                await using (var fs = File.OpenRead(item.FilePath))
                {
                    var uploaded = await _api.UploadAttachmentAsync(item.TaskId, fs, meta.FileName, meta.ContentType, meta.Kind,
                        meta.Latitude, meta.Longitude, meta.CapturedAt, meta.ClientId);
                    // Yerel data: URL yerine sunucu URL'ini kullan (önbellek şişmesin).
                    await UpdateCachedTaskAsync(item.TaskId, t => t with
                    {
                        Attachments = t.Attachments.Select(a => a.Id == meta.ClientId ? uploaded : a).ToList()
                    }, notify: false);
                }
                break;
            case OutboxKind.DeleteAttachment:
                await _api.DeleteAttachmentAsync(item.TaskId, Guid.Parse(item.Payload));
                break;
            case OutboxKind.Shift:
                if (item.Payload == "start") await _api.StartShiftAsync();
                else await _api.EndShiftAsync();
                break;
        }
    }

    /// <summary>Sunucudan güncel görev listesini çeker. Bekleyen yerel değişikliği olan görevlerin yerel hali korunur.</summary>
    private async Task PullAsync()
    {
        SyncResponse data;
        try
        {
            data = await _api.SyncAsync();
            SetOnline(true);
        }
        catch (ApiException ex)
        {
            if (ex.IsNetworkError) SetOnline(false);
            else LastError = ex.Message;
            Changed?.Invoke();
            return;
        }

        var db = await _local.Db();
        var pendingTaskIds = (await db.Table<OutboxItem>().ToListAsync()).Select(o => o.TaskId).ToHashSet();
        var existing = (await db.Table<CachedTask>().ToListAsync()).ToDictionary(r => r.Id);

        await db.RunInTransactionAsync(tx =>
        {
            foreach (var row in existing.Values.Where(r => !pendingTaskIds.Contains(r.Id)))
                tx.Delete(row);
            foreach (var t in data.Tasks.Where(t => !pendingTaskIds.Contains(t.Id)))
                tx.InsertOrReplace(new CachedTask { Id = t.Id, Json = JsonSerializer.Serialize(t, Json) });
            tx.DeleteAll<CachedType>();
            foreach (var type in data.TaskTypes)
                tx.InsertOrReplace(new CachedType { Id = type.Id, Json = JsonSerializer.Serialize(type, Json) });
        });
        await _local.SetValueAsync("shift", data.ActiveShift is null ? null : JsonSerializer.Serialize(data.ActiveShift, Json));
        LastSync = data.ServerTime;
        if (pendingTaskIds.Count == 0) LastError = null;
        Changed?.Invoke();
    }

    // ---------- Yardımcılar ----------
    private async Task EnqueueAsync(OutboxItem item, bool push = true)
    {
        await (await _local.Db()).InsertAsync(item);
        await RefreshPendingCountAsync();
        Changed?.Invoke();
        if (push) await PushOutboxAsync();
    }

    private async Task RefreshPendingCountAsync() => PendingCount = await (await _local.Db()).Table<OutboxItem>().CountAsync();

    private async Task UpdateCachedTaskAsync(Guid id, Func<WorkTaskDetailDto, WorkTaskDetailDto> update, bool notify = true)
    {
        var db = await _local.Db();
        var row = await db.FindAsync<CachedTask>(id);
        if (row is null) return;
        var task = update(JsonSerializer.Deserialize<WorkTaskDetailDto>(row.Json, Json)!);
        row.Json = JsonSerializer.Serialize(task, Json);
        await db.UpdateAsync(row);
        if (notify) Changed?.Invoke();
    }

    private void SetOnline(bool online)
    {
        if (IsOnline == online) return;
        IsOnline = online;
        Changed?.Invoke();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        var online = e.NetworkAccess == NetworkAccess.Internet;
        SetOnline(online);
        if (online && _session.Current is not null) _ = SyncAsync();
    }

    public void Dispose()
    {
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        _timer.Dispose();
    }

    private record AttachmentMeta(Guid ClientId, AttachmentKind Kind, string FileName, string ContentType,
        double? Latitude, double? Longitude, DateTime CapturedAt);
}
