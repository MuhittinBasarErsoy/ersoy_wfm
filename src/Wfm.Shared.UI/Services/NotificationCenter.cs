using Wfm.Application.Contracts;
using Wfm.Client;

namespace Wfm.Shared.UI.Services;

/// <summary>
/// Oturum açıldığında SignalR'a bağlanır, bildirimleri toplar ve okunmamış sayısını tutar.
/// Saha çalışanı için gelen görev değişikliklerini <see cref="IFieldService"/>'e iletir.
/// </summary>
public sealed class NotificationCenter(WfmApiClient api, WfmRealtime realtime) : IDisposable
{
    private bool _started;
    private readonly List<NotificationDto> _items = [];

    public IReadOnlyList<NotificationDto> Items => _items;
    public int Unread => _items.Count(n => n.ReadAt is null);
    public bool Connected => realtime.IsConnected;

    public event Action? Changed;
    /// <summary>Yeni bildirim geldiğinde (snackbar/yerel bildirim göstermek için).</summary>
    public event Action<NotificationDto>? Received;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        realtime.NotificationReceived += OnReceived;
        realtime.StateChanged += OnState;
        await realtime.StartAsync();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var list = await api.GetNotificationsAsync();
            _items.Clear();
            _items.AddRange(list);
            Changed?.Invoke();
        }
        catch (ApiException) { /* offline */ }
    }

    public async Task MarkAllReadAsync()
    {
        try
        {
            await api.MarkNotificationsReadAsync();
            for (var i = 0; i < _items.Count; i++)
                if (_items[i].ReadAt is null) _items[i] = _items[i] with { ReadAt = DateTime.UtcNow };
            Changed?.Invoke();
        }
        catch (ApiException) { }
    }

    public async Task MarkReadAsync(Guid id)
    {
        var i = _items.FindIndex(n => n.Id == id);
        if (i < 0 || _items[i].ReadAt is not null) return;
        _items[i] = _items[i] with { ReadAt = DateTime.UtcNow };
        Changed?.Invoke();
        try { await api.MarkNotificationReadAsync(id); }
        catch (ApiException) { /* offline: bir sonraki yenilemede düzelir */ }
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;
        realtime.NotificationReceived -= OnReceived;
        realtime.StateChanged -= OnState;
        _items.Clear();
        await realtime.StopAsync();
    }

    private void OnReceived(NotificationDto n)
    {
        if (_items.Any(x => x.Id == n.Id)) return;
        _items.Insert(0, n);
        Received?.Invoke(n);
        Changed?.Invoke();
    }

    private void OnState(bool _) => Changed?.Invoke();

    public void Dispose()
    {
        realtime.NotificationReceived -= OnReceived;
        realtime.StateChanged -= OnState;
    }
}
