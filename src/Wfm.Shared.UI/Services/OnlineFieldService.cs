using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Enums;

namespace Wfm.Shared.UI.Services;

/// <summary>Web için saha servisi: önbellek yok, her işlem doğrudan API'ye gider.</summary>
public class OnlineFieldService(WfmApiClient api) : IFieldService
{
    private SyncResponse? _data;

    public event Action? Changed;
    public bool IsOnline { get; private set; } = true;
    public int PendingCount => 0;
    public DateTime? LastSync { get; private set; }
    public string? LastError { get; private set; }

    public async Task<IReadOnlyList<WorkTaskDetailDto>> GetTasksAsync()
    {
        if (_data is null) await SyncAsync();
        return _data?.Tasks ?? [];
    }

    public async Task<WorkTaskDetailDto?> GetTaskAsync(Guid id)
    {
        try { return await api.GetTaskAsync(id); }
        catch (ApiException) { return null; }
    }

    public async Task<TaskTypeDto?> GetTaskTypeAsync(Guid id)
    {
        if (_data is null) await SyncAsync();
        return _data?.TaskTypes.FirstOrDefault(t => t.Id == id);
    }

    public async Task SyncAsync()
    {
        try
        {
            _data = await api.SyncAsync();
            LastSync = DateTime.UtcNow;
            IsOnline = true;
            LastError = null;
        }
        catch (ApiException ex)
        {
            IsOnline = !ex.IsNetworkError;
            LastError = ex.Message;
        }
        Changed?.Invoke();
    }

    public async Task ChangeStatusAsync(Guid taskId, ChangeStatusRequest request)
    {
        await api.ChangeStatusAsync(taskId, request);
        await SyncAsync();
    }

    public async Task AddAttachmentAsync(Guid taskId, CapturedFile file, AttachmentKind kind, GeoPoint? location)
    {
        using var ms = new MemoryStream(file.Data);
        await api.UploadAttachmentAsync(taskId, ms, file.FileName, file.ContentType, kind, location?.Latitude, location?.Longitude,
            DateTime.UtcNow, Guid.NewGuid());
        Changed?.Invoke();
    }

    public async Task RemoveAttachmentAsync(Guid taskId, Guid attachmentId)
    {
        await api.DeleteAttachmentAsync(taskId, attachmentId);
        Changed?.Invoke();
    }

    public Task<ShiftDto?> GetShiftAsync() => api.GetShiftAsync();

    public async Task<ShiftDto?> SetShiftAsync(bool onShift) => onShift ? await api.StartShiftAsync() : await api.EndShiftAsync();

    public async Task OnServerTaskChangedAsync(WorkTaskDto task) => await SyncAsync();

    public Task ClearAsync()
    {
        _data = null;
        return Task.CompletedTask;
    }
}
