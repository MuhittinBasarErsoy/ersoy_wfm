using System.Net;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Enums;

namespace Wfm.Api.Tests;

/// <summary>Arayüz iyileştirmeleriyle gelen uç noktalar: yorumlar, takip linki, gecikme, sıralama, profil, şirket ayarları.</summary>
public class UxFeatureTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string FloristAdmin = "yonetici@cicek.local";
    private const string FloristDispatcher = "dispecer@cicek.local";
    private const string FloristWorker = "kurye1@cicek.local";
    private const string FloristWorker2 = "kurye2@cicek.local";
    private const string FloristViewer = "izleyici@cicek.local";
    private const string RepairDispatcher = "dispecer@tamir.local";

    [Fact]
    public async Task Comments_flow_between_dispatcher_and_assignee_with_notification()
    {
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var worker = await api.LoginAs(FloristWorker);
        var other = await api.LoginAs(FloristWorker2);
        var me = await worker.MeAsync();
        var task = await dispatcher.CreateTaskAsync(await NewDelivery(dispatcher, me.Id));

        await dispatcher.AddCommentAsync(task.Id, "Kapı kodu 1234");
        var reply = await worker.AddCommentAsync(task.Id, "Tamam, 10 dk içinde oradayım");
        Assert.Equal(me.FullName, reply.UserName);

        var thread = await dispatcher.GetCommentsAsync(task.Id);
        Assert.Equal(["Kapı kodu 1234", "Tamam, 10 dk içinde oradayım"], thread.Select(c => c.Body));

        // Çalışan, dispeçerin mesajı için "comment" türünde bildirim alır.
        var notes = await worker.GetNotificationsAsync();
        Assert.Contains(notes, n => n.Kind == "comment" && n.WorkTaskId == task.Id);

        // Görev başkasına atanmışsa diğer çalışan yazamaz/göremez; boş mesaj reddedilir.
        await Assert.ThrowsAsync<ApiException>(() => other.AddCommentAsync(task.Id, "selam"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => dispatcher.AddCommentAsync(task.Id, "   "));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);

        // Başka şirket göremez.
        var repair = await api.LoginAs(RepairDispatcher);
        var nf = await Assert.ThrowsAsync<ApiException>(() => repair.GetCommentsAsync(task.Id));
        Assert.Equal(HttpStatusCode.NotFound, nf.Status);
    }

    [Fact]
    public async Task Public_tracking_link_shows_limited_info_without_login()
    {
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var worker = await api.LoginAs(FloristWorker);
        var me = await worker.MeAsync();
        var task = await dispatcher.CreateTaskAsync(await NewDelivery(dispatcher, me.Id));

        var link = await dispatcher.CreateTrackingLinkAsync(task.Id);
        Assert.Equal(link.Token, (await dispatcher.CreateTrackingLinkAsync(task.Id)).Token); // aynı link tekrar üretilmez

        // Saha çalışanı link oluşturamaz.
        await Assert.ThrowsAsync<ApiException>(() => worker.CreateTrackingLinkAsync(task.Id));

        var anon = AnonymousClient();
        var view = await anon.GetPublicTrackingAsync(link.Token);
        Assert.Equal(task.Title, view.Title);
        Assert.Equal(WorkTaskStatus.Assigned, view.Status);
        Assert.Null(view.WorkerLatitude); // yolda değilken konum paylaşılmaz
        Assert.Equal(me.FullName.Split(' ')[0], view.WorkerFirstName);

        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.EnRoute });
        await worker.SendPingsAsync([new LocationPingDto(40.99, 29.02, 5, null, null, 80, DateTime.UtcNow)]);
        view = await anon.GetPublicTrackingAsync(link.Token);
        Assert.Equal(WorkTaskStatus.EnRoute, view.Status);
        Assert.Equal(40.99, view.WorkerLatitude);

        var ex = await Assert.ThrowsAsync<ApiException>(() => anon.GetPublicTrackingAsync("0123456789abcdef0123456789abcdef"));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task Overdue_filter_report_and_sorting()
    {
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var req = await NewDelivery(dispatcher, null);
        req.Title = "Geciken test görevi";
        req.ScheduledStart = DateTime.UtcNow.AddHours(-3);
        req.ScheduledEnd = DateTime.UtcNow.AddHours(-2);
        var late = await dispatcher.CreateTaskAsync(req);
        Assert.True(late.IsOverdue);

        var overdue = await dispatcher.GetTasksAsync(new TaskQuery { Overdue = true, PageSize = 500 });
        Assert.Contains(overdue.Items, t => t.Id == late.Id);
        Assert.All(overdue.Items, t => Assert.True(t.IsOverdue));

        var report = await dispatcher.GetReportAsync(7);
        Assert.True(report.OverdueCount >= 1);
        Assert.True(report.UnassignedCount >= 1);
        Assert.Equal(7, report.Daily!.Count);
        Assert.NotEmpty(report.Types!);

        var byScheduled = await dispatcher.GetTasksAsync(new TaskQuery { Sort = "scheduled", Desc = true, PageSize = 500 });
        var keys = byScheduled.Items.Select(t => t.ScheduledStart ?? t.CreatedAt).ToList();
        Assert.Equal(keys.OrderByDescending(k => k), keys);
        var byAssignee = await dispatcher.GetTasksAsync(new TaskQuery { Sort = "assignee", PageSize = 500 });
        // Atanmamış görevler sonda.
        var firstUnassigned = byAssignee.Items.FindIndex(t => t.AssigneeId is null);
        Assert.True(firstUnassigned < 0 || byAssignee.Items.Skip(firstUnassigned).All(t => t.AssigneeId is null));
    }

    [Fact]
    public async Task Notifications_can_be_marked_read_one_by_one()
    {
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var worker = await api.LoginAs(FloristWorker2);
        var me = await worker.MeAsync();
        var task = await dispatcher.CreateTaskAsync(await NewDelivery(dispatcher, me.Id));

        var n = (await worker.GetNotificationsAsync(unreadOnly: true)).First(x => x.WorkTaskId == task.Id);
        Assert.Equal("assigned", n.Kind);
        await worker.MarkNotificationReadAsync(n.Id);
        Assert.DoesNotContain(await worker.GetNotificationsAsync(unreadOnly: true), x => x.Id == n.Id);
    }

    [Fact]
    public async Task Profile_password_and_tenant_settings()
    {
        var viewer = await api.LoginAs(FloristViewer);
        var updated = await viewer.UpdateProfileAsync(new UpdateProfileRequest("İzleyici Yeni Ad", "05550000000"));
        Assert.Equal("İzleyici Yeni Ad", updated.FullName);

        var wrong = await Assert.ThrowsAsync<ApiException>(() => viewer.ChangePasswordAsync(new ChangePasswordRequest("yanlis", "Yeni123")));
        Assert.Contains("Mevcut parola", wrong.Message);
        await viewer.ChangePasswordAsync(new ChangePasswordRequest("Demo123!", "Yeni123"));
        await viewer.ChangePasswordAsync(new ChangePasswordRequest("Yeni123", "Demo123!"));

        // Şirket ayarlarını yalnızca şirket yöneticisi değiştirir.
        await Assert.ThrowsAsync<ApiException>(() => viewer.UpdateMyTenantAsync(new UpdateTenantSettingsRequest("X", 41, 29, null, null)));
        var admin = await api.LoginAs(FloristAdmin);
        var tenant = await admin.MyTenantAsync();
        var bad = await Assert.ThrowsAsync<ApiException>(() => admin.UpdateMyTenantAsync(new UpdateTenantSettingsRequest(tenant.Name, 41, 29, "kırmızı", null)));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var saved = await admin.UpdateMyTenantAsync(new UpdateTenantSettingsRequest(tenant.Name, 41.05, 29.01, "#C62828", "https://example.com/logo.png"));
        Assert.Equal("#c62828", saved.BrandColor);
        Assert.Equal("https://example.com/logo.png", (await viewer.MyTenantAsync()).LogoUrl);
        await admin.UpdateMyTenantAsync(new UpdateTenantSettingsRequest(tenant.Name, tenant.DefaultLatitude, tenant.DefaultLongitude, null, null));
    }

    [Fact]
    public async Task Saved_filters_can_be_shared_with_team()
    {
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var admin = await api.LoginAs(FloristAdmin);
        var viewer = await api.LoginAs(FloristViewer);

        await dispatcher.SaveFilterAsync(new SaveFilterRequest("Kişisel", "?overdue=1", false));
        var shared = await dispatcher.SaveFilterAsync(new SaveFilterRequest("Ekip: geciken", "?overdue=1&open=1", true));

        var mine = await dispatcher.GetSavedFiltersAsync();
        Assert.Contains(mine, f => f.Name == "Kişisel" && f.IsMine);
        var seenByAdmin = await admin.GetSavedFiltersAsync();
        Assert.DoesNotContain(seenByAdmin, f => f.Name == "Kişisel");
        Assert.Contains(seenByAdmin, f => f.Id == shared.Id && !f.IsMine && f.OwnerName is not null);

        // Aynı adla kaydetmek günceller.
        await dispatcher.SaveFilterAsync(new SaveFilterRequest("Ekip: geciken", "?overdue=1", true));
        Assert.Equal("?overdue=1", (await admin.GetSavedFiltersAsync()).Single(f => f.Id == shared.Id).Query);

        // İzleyici başkasının filtresini silemez; yönetici silebilir.
        await Assert.ThrowsAsync<ApiException>(() => viewer.DeleteSavedFilterAsync(shared.Id));
        await admin.DeleteSavedFilterAsync(shared.Id);
        Assert.DoesNotContain(await dispatcher.GetSavedFiltersAsync(), f => f.Id == shared.Id);

        // Başka şirket görmez.
        var repair = await api.LoginAs(RepairDispatcher);
        Assert.DoesNotContain(await repair.GetSavedFiltersAsync(), f => f.Name == "Kişisel");
    }

    [Fact]
    public async Task Forgot_password_notifies_tenant_admin_without_revealing_accounts()
    {
        var anon = AnonymousClient();
        await anon.ForgotPasswordAsync("yok-boyle-biri@cicek.local"); // var olmayan hesap da aynı yanıtı alır
        await anon.ForgotPasswordAsync(FloristWorker2);
        await anon.ForgotPasswordAsync(FloristWorker2); // 10 dk içinde tekrar: çift bildirim yok

        var admin = await api.LoginAs(FloristAdmin);
        var resets = (await admin.GetNotificationsAsync()).Where(n => n.Kind == "password_reset" && n.Body.Contains(FloristWorker2)).ToList();
        Assert.Single(resets);
    }

    [Fact]
    public async Task Login_brand_and_uploaded_logo_are_public()
    {
        var anon = AnonymousClient();
        var brand = await anon.GetBrandAsync("cicek-dunyasi");
        Assert.Equal("Çiçek Dünyası", brand.Name);
        await Assert.ThrowsAsync<ApiException>(() => anon.GetBrandAsync("system"));

        var admin = await api.LoginAs(FloristAdmin);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var bad = await Assert.ThrowsAsync<ApiException>(() => admin.UploadLogoAsync(new MemoryStream(png), "logo.svg", "image/svg+xml"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var tenant = await admin.UploadLogoAsync(new MemoryStream(png), "logo.png", "image/png");
        Assert.StartsWith("/api/public/tenant-logo/", tenant.LogoUrl);

        using var raw = api.CreateClient();
        var res = await raw.GetAsync(tenant.LogoUrl);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(tenant.LogoUrl, (await anon.GetBrandAsync("cicek-dunyasi")).LogoUrl);

        // Logo kaldırılınca dosya da silinir.
        await admin.UpdateMyTenantAsync(new UpdateTenantSettingsRequest(tenant.Name, tenant.DefaultLatitude, tenant.DefaultLongitude, null, null));
        Assert.Equal(HttpStatusCode.NotFound, (await raw.GetAsync(tenant.LogoUrl)).StatusCode);
    }

    [Fact]
    public async Task Report_accepts_custom_date_range()
    {
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-9);
        var report = await dispatcher.GetReportAsync(from, to);
        Assert.Equal(10, report.Daily!.Count);
        Assert.Equal(from, report.Daily[0].Day);
        Assert.Equal(to, report.Daily[^1].Day);
    }

    private WfmApiClient AnonymousClient()
    {
        var options = new WfmClientOptions { ApiBaseUrl = api.Server.BaseAddress.ToString() };
        var http = new HttpClient(api.Server.CreateHandler()) { BaseAddress = api.Server.BaseAddress };
        return new WfmApiClient(http, options);
    }

    private static async Task<SaveWorkTaskRequest> NewDelivery(WfmApiClient dispatcher, Guid? assignee)
    {
        var typeId = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Çiçek Teslimatı").Id;
        return new SaveWorkTaskRequest
        {
            TaskTypeId = typeId,
            Title = "UX test teslimatı",
            Address = "Moda, Kadıköy",
            Latitude = 40.985,
            Longitude = 29.026,
            AssigneeId = assignee,
            CustomFieldValues = new() { ["recipient_name"] = "Test", ["flower_type"] = "Orkide" }
        };
    }
}
