using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wfm.Application.Contracts;
using Wfm.Domain.Enums;

namespace Wfm.Client;

public static class WfmJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>API'den dönen hata. <see cref="IsNetworkError"/> true ise sunucuya ulaşılamamıştır (offline).</summary>
public class ApiException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
    public bool IsNetworkError => Status is null;
    public bool IsConflict => Status == HttpStatusCode.Conflict;
}

/// <summary>Web ve mobil istemcilerin ortak kullandığı tip güvenli API istemcisi.</summary>
public class WfmApiClient(HttpClient http, WfmClientOptions options)
{
    public string BaseUrl => options.ApiBaseUrl.TrimEnd('/');

    /// <summary>Sunucunun döndürdüğü göreli dosya URL'ini mutlak yapar.</summary>
    public string AbsoluteUrl(string relative) =>
        relative.StartsWith("http") ? relative : (options.PublicBaseUrl ?? BaseUrl).TrimEnd('/') + relative;

    // ---------- Auth ----------
    public Task<AuthResponse> LoginAsync(LoginRequest req) => Post<AuthResponse>("/api/auth/login", req);
    public Task LogoutAsync(string refreshToken) => Send(HttpMethod.Post, "/api/auth/logout", new RefreshRequest(refreshToken));
    public Task<UserDto> MeAsync() => Get<UserDto>("/api/auth/me");
    public Task<TenantDto> MyTenantAsync() => Get<TenantDto>("/api/auth/tenant");

    // ---------- Tenants ----------
    public Task<List<TenantDto>> GetTenantsAsync() => Get<List<TenantDto>>("/api/tenants");
    public Task<TenantDto> CreateTenantAsync(CreateTenantRequest req) => Post<TenantDto>("/api/tenants", req);
    public Task SetTenantActiveAsync(Guid id, bool active) => Send(HttpMethod.Put, $"/api/tenants/{id}/active?active={active}", null);

    // ---------- Users / Teams ----------
    public Task<List<UserDto>> GetUsersAsync(string? role = null) =>
        Get<List<UserDto>>("/api/users" + (role is null ? "" : $"?role={role}"));
    public Task<UserDto> CreateUserAsync(CreateUserRequest req) => Post<UserDto>("/api/users", req);
    public Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest req) => Put<UserDto>($"/api/users/{id}", req);
    public Task<List<TeamDto>> GetTeamsAsync() => Get<List<TeamDto>>("/api/teams");
    public Task<TeamDto> CreateTeamAsync(SaveTeamRequest req) => Post<TeamDto>("/api/teams", req);
    public Task<TeamDto> UpdateTeamAsync(Guid id, SaveTeamRequest req) => Put<TeamDto>($"/api/teams/{id}", req);
    public Task DeleteTeamAsync(Guid id) => Send(HttpMethod.Delete, $"/api/teams/{id}", null);

    // ---------- Task types ----------
    public Task<List<TaskTypeDto>> GetTaskTypesAsync(bool includeInactive = false) =>
        Get<List<TaskTypeDto>>($"/api/task-types?includeInactive={includeInactive}");
    public Task<TaskTypeDto> CreateTaskTypeAsync(SaveTaskTypeRequest req) => Post<TaskTypeDto>("/api/task-types", req);
    public Task<TaskTypeDto> UpdateTaskTypeAsync(Guid id, SaveTaskTypeRequest req) => Put<TaskTypeDto>($"/api/task-types/{id}", req);

    // ---------- Tasks ----------
    public Task<PagedResult<WorkTaskDto>> GetTasksAsync(TaskQuery q)
    {
        var qs = new List<string> { $"page={q.Page}", $"pageSize={q.PageSize}", $"onlyOpen={q.OnlyOpen}" };
        if (q.Statuses is { Count: > 0 }) qs.AddRange(q.Statuses.Select(s => $"statuses={s}"));
        if (q.AssigneeId is { } a) qs.Add($"assigneeId={a}");
        if (q.TaskTypeId is { } t) qs.Add($"taskTypeId={t}");
        if (q.From is { } f) qs.Add($"from={Uri.EscapeDataString(f.ToUniversalTime().ToString("O"))}");
        if (q.To is { } to) qs.Add($"to={Uri.EscapeDataString(to.ToUniversalTime().ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(q.Search)) qs.Add($"search={Uri.EscapeDataString(q.Search)}");
        return Get<PagedResult<WorkTaskDto>>("/api/tasks?" + string.Join('&', qs));
    }
    public Task<WorkTaskDetailDto> GetTaskAsync(Guid id) => Get<WorkTaskDetailDto>($"/api/tasks/{id}");
    public Task<WorkTaskDto> CreateTaskAsync(SaveWorkTaskRequest req) => Post<WorkTaskDto>("/api/tasks", req);
    public Task<WorkTaskDto> UpdateTaskAsync(Guid id, SaveWorkTaskRequest req) => Put<WorkTaskDto>($"/api/tasks/{id}", req);
    public Task<WorkTaskDto> AssignTaskAsync(Guid id, Guid? assigneeId) => Post<WorkTaskDto>($"/api/tasks/{id}/assign", new AssignRequest(assigneeId));
    public Task<WorkTaskDto> ChangeStatusAsync(Guid id, ChangeStatusRequest req) => Post<WorkTaskDto>($"/api/tasks/{id}/status", req);
    public Task DeleteTaskAsync(Guid id) => Send(HttpMethod.Delete, $"/api/tasks/{id}", null);

    public async Task<AttachmentDto> UploadAttachmentAsync(Guid taskId, Stream content, string fileName, string contentType,
        AttachmentKind kind, double? lat, double? lng, DateTime? capturedAt, Guid? clientId)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new(contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(kind.ToString()), "kind");
        if (lat is { } la) form.Add(new StringContent(la.ToString(System.Globalization.CultureInfo.InvariantCulture)), "latitude");
        if (lng is { } lo) form.Add(new StringContent(lo.ToString(System.Globalization.CultureInfo.InvariantCulture)), "longitude");
        if (capturedAt is { } c) form.Add(new StringContent(c.ToUniversalTime().ToString("O")), "capturedAt");
        if (clientId is { } id) form.Add(new StringContent(id.ToString()), "clientId");
        return await Read<AttachmentDto>(() => http.PostAsync($"/api/tasks/{taskId}/attachments", form));
    }
    public Task DeleteAttachmentAsync(Guid taskId, Guid attachmentId) =>
        Send(HttpMethod.Delete, $"/api/tasks/{taskId}/attachments/{attachmentId}", null);

    // ---------- Tracking ----------
    public Task SendPingsAsync(IReadOnlyList<LocationPingDto> pings) => Send(HttpMethod.Post, "/api/tracking/pings", pings);
    public Task<List<WorkerLocationDto>> GetLiveLocationsAsync() => Get<List<WorkerLocationDto>>("/api/tracking/live");
    public Task<List<LocationPingDto>> GetHistoryAsync(Guid userId, DateTime from, DateTime to) =>
        Get<List<LocationPingDto>>($"/api/tracking/history/{userId}?from={Uri.EscapeDataString(from.ToUniversalTime().ToString("O"))}&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("O"))}");
    public Task<ShiftDto?> GetShiftAsync() => GetOrNull<ShiftDto>("/api/tracking/shift");
    public Task<ShiftDto> StartShiftAsync() => Post<ShiftDto>("/api/tracking/shift/start", null);
    public Task<ShiftDto?> EndShiftAsync() => PostOrNull<ShiftDto>("/api/tracking/shift/end");

    // ---------- Notifications / Sync / Reports ----------
    public Task<List<NotificationDto>> GetNotificationsAsync(bool unreadOnly = false) =>
        Get<List<NotificationDto>>($"/api/notifications?unreadOnly={unreadOnly}");
    public Task MarkNotificationsReadAsync() => Send(HttpMethod.Post, "/api/notifications/read-all", null);
    public Task<SyncResponse> SyncAsync() => Get<SyncResponse>("/api/sync");
    public Task<ReportSummaryDto> GetReportAsync(int days = 30) => Get<ReportSummaryDto>($"/api/reports/summary?days={days}");

    // ---------- HTTP yardımcıları ----------
    private Task<T> Get<T>(string url) => Read<T>(() => http.GetAsync(url));
    private Task<T> Post<T>(string url, object? body) => Read<T>(() => http.PostAsJsonAsync(url, body, WfmJson.Options));
    private Task<T> Put<T>(string url, object? body) => Read<T>(() => http.PutAsJsonAsync(url, body, WfmJson.Options));

    private async Task<T?> GetOrNull<T>(string url) where T : class
    {
        var res = await Execute(() => http.GetAsync(url));
        return res.StatusCode == HttpStatusCode.NoContent ? null : await res.Content.ReadFromJsonAsync<T>(WfmJson.Options);
    }

    private async Task<T?> PostOrNull<T>(string url) where T : class
    {
        var res = await Execute(() => http.PostAsync(url, null));
        return res.StatusCode == HttpStatusCode.NoContent ? null : await res.Content.ReadFromJsonAsync<T>(WfmJson.Options);
    }

    private async Task Send(HttpMethod method, string url, object? body)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, options: WfmJson.Options);
        (await Execute(() => http.SendAsync(req))).Dispose();
    }

    private async Task<T> Read<T>(Func<Task<HttpResponseMessage>> call)
    {
        using var res = await Execute(call);
        return (await res.Content.ReadFromJsonAsync<T>(WfmJson.Options))!;
    }

    private static async Task<HttpResponseMessage> Execute(Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage res;
        try
        {
            res = await call();
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("Sunucuya ulaşılamıyor. İnternet bağlantınızı kontrol edin.", null, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new ApiException("Sunucu yanıt vermedi.", null, ex);
        }
        if (res.IsSuccessStatusCode) return res;

        string message;
        try
        {
            message = (await res.Content.ReadFromJsonAsync<ApiError>(WfmJson.Options))?.Message ?? res.ReasonPhrase ?? "Hata";
        }
        catch
        {
            message = res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Oturumunuzun süresi doldu, tekrar giriş yapın.",
                HttpStatusCode.Forbidden => "Bu işlem için yetkiniz yok.",
                HttpStatusCode.NotFound => "Kayıt bulunamadı.",
                _ => $"İstek başarısız ({(int)res.StatusCode})."
            };
        }
        var status = res.StatusCode;
        res.Dispose();
        throw new ApiException(message, status);
    }
}
