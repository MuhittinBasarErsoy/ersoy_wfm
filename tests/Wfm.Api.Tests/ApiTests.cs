using System.Net;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Enums;

namespace Wfm.Api.Tests;

public class ApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string FloristDispatcher = "dispecer@cicek.local";
    private const string FloristWorker = "kurye1@cicek.local";
    private const string FloristWorker2 = "kurye2@cicek.local";
    private const string FloristViewer = "izleyici@cicek.local";
    private const string RepairDispatcher = "dispecer@tamir.local";

    [Fact]
    public async Task Tenants_are_isolated()
    {
        var florist = await api.LoginAs(FloristDispatcher);
        var repair = await api.LoginAs(RepairDispatcher);

        var floristTasks = await florist.GetTasksAsync(new TaskQuery());
        var repairTasks = await repair.GetTasksAsync(new TaskQuery());

        Assert.NotEmpty(floristTasks.Items);
        Assert.NotEmpty(repairTasks.Items);
        Assert.DoesNotContain(floristTasks.Items, t => repairTasks.Items.Any(r => r.Id == t.Id));

        var ex = await Assert.ThrowsAsync<ApiException>(() => florist.GetTaskAsync(repairTasks.Items[0].Id));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);

        var floristTypes = await florist.GetTaskTypesAsync();
        Assert.All(floristTypes, t => Assert.NotEqual("Arıza Onarımı", t.Name));

        // Başka şirketin çalışanına görev atanamaz.
        var repairWorker = (await repair.GetUsersAsync("FieldWorker"))[0];
        var create = NewDelivery(floristTypes.First(t => t.Name == "Çiçek Teslimatı").Id);
        create.AssigneeId = repairWorker.Id;
        await Assert.ThrowsAsync<ApiException>(() => florist.CreateTaskAsync(create));
    }

    [Fact]
    public async Task Roles_are_enforced()
    {
        var worker = await api.LoginAs(FloristWorker);
        var viewer = await api.LoginAs(FloristViewer);
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var typeId = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Çiçek Teslimatı").Id;

        var ex = await Assert.ThrowsAsync<ApiException>(() => worker.CreateTaskAsync(NewDelivery(typeId)));
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
        ex = await Assert.ThrowsAsync<ApiException>(() => viewer.CreateTaskAsync(NewDelivery(typeId)));
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
        ex = await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreateUserAsync(
            new CreateUserRequest("x@cicek.local", "Demo123!", "X", null, "FieldWorker", null)));
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);

        // Saha çalışanı yalnızca kendi görevlerini görür.
        var me = await worker.MeAsync();
        var mine = await worker.GetTasksAsync(new TaskQuery());
        Assert.NotEmpty(mine.Items);
        Assert.All(mine.Items, t => Assert.Equal(me.Id, t.AssigneeId));

        // Viewer raporları görebilir.
        var report = await viewer.GetReportAsync();
        Assert.True(report.CountsByStatus.Values.Sum() > 0);
    }

    [Fact]
    public async Task Field_worker_completes_task_with_proof()
    {
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var worker = await api.LoginAs(FloristWorker2);
        var other = await api.LoginAs(FloristWorker);
        var me = await worker.MeAsync();
        var typeId = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Çiçek Teslimatı").Id;

        var req = NewDelivery(typeId);
        req.AssigneeId = me.Id;
        var task = await dispatcher.CreateTaskAsync(req);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);

        // Başka çalışan bu görevi değiştiremez / göremez.
        await Assert.ThrowsAsync<ApiException>(() => other.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted }));

        // Geçersiz geçiş.
        var ex = await Assert.ThrowsAsync<ApiException>(() => worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Completed }));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);

        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        // Tekrar gönderim idempotent.
        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.EnRoute });
        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.OnSite });

        // Kanıt olmadan tamamlanamaz.
        ex = await Assert.ThrowsAsync<ApiException>(() => worker.ChangeStatusAsync(task.Id, new()
        {
            Status = WorkTaskStatus.Completed,
            CustomFieldValues = new() { ["received_by"] = "Kapıcı" }
        }));
        Assert.Contains("imza", ex.Message);

        var clientId = Guid.NewGuid();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        await worker.UploadAttachmentAsync(task.Id, new MemoryStream(png), "photo.png", "image/png", AttachmentKind.Photo, 40.99, 29.02, DateTime.UtcNow, clientId);
        // Aynı clientId ile tekrar yükleme çift kayıt oluşturmaz.
        await worker.UploadAttachmentAsync(task.Id, new MemoryStream(png), "photo.png", "image/png", AttachmentKind.Photo, 40.99, 29.02, DateTime.UtcNow, clientId);
        await worker.UploadAttachmentAsync(task.Id, new MemoryStream(png), "sig.png", "image/png", AttachmentKind.Signature, null, null, DateTime.UtcNow, Guid.NewGuid());

        var done = await worker.ChangeStatusAsync(task.Id, new()
        {
            Status = WorkTaskStatus.Completed,
            CustomFieldValues = new() { ["received_by"] = "Kapıcı" }
        });
        Assert.Equal(WorkTaskStatus.Completed, done.Status);

        var detail = await dispatcher.GetTaskAsync(task.Id);
        Assert.Equal(2, detail.Attachments.Count);
        Assert.Equal("Kapıcı", detail.CustomFieldValues["received_by"]);
        Assert.Contains(detail.Events, e => e.ToStatus == WorkTaskStatus.Completed);

        // İmzalı dosya URL'i çalışır, imzasız çalışmaz.
        using var raw = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await raw.GetAsync(detail.Attachments[0].Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.GetAsync(detail.Attachments[0].Url.Split('&')[0] + "&sig=bad")).StatusCode);
    }

    [Fact]
    public async Task Location_pings_are_visible_to_dispatcher()
    {
        var worker = await api.LoginAs(FloristWorker);
        var dispatcher = await api.LoginAs(FloristDispatcher);
        await worker.StartShiftAsync();
        await worker.SendPingsAsync([new(40.98, 29.03, 5, 1.2, null, 80, DateTime.UtcNow.AddSeconds(-30)),
                                     new(40.981, 29.031, 5, 1.2, null, 79, DateTime.UtcNow)]);

        var live = await dispatcher.GetLiveLocationsAsync();
        var me = await worker.MeAsync();
        var loc = Assert.Single(live, l => l.UserId == me.Id);
        Assert.True(loc.OnShift);
        Assert.Equal(40.981, loc.Latitude, 3);

        var history = await dispatcher.GetHistoryAsync(me.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(2, history.Count);

        // Başka şirket göremez.
        var repair = await api.LoginAs(RepairDispatcher);
        Assert.DoesNotContain(await repair.GetLiveLocationsAsync(), l => l.UserId == me.Id);
        Assert.Empty(await repair.GetHistoryAsync(me.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async Task Required_custom_fields_are_validated()
    {
        var dispatcher = await api.LoginAs(FloristDispatcher);
        var typeId = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Çiçek Teslimatı").Id;
        var req = NewDelivery(typeId);
        req.CustomFieldValues.Remove("recipient_name");
        var ex = await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreateTaskAsync(req));
        Assert.Contains("Alıcı adı", ex.Message);
    }

    private static SaveWorkTaskRequest NewDelivery(Guid typeId) => new()
    {
        TaskTypeId = typeId,
        Title = "Test teslimatı",
        Address = "Moda, Kadıköy",
        Latitude = 40.985,
        Longitude = 29.026,
        CustomFieldValues = new() { ["recipient_name"] = "Test", ["flower_type"] = "Orkide" }
    };
}
