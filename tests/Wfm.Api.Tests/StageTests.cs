using System.Net;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Api.Tests;

/// <summary>Görev tipine özel aşamalar ve saha ziyareti gerektirmeyen (masa başı) görevler.</summary>
public class StageTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string RepairAdmin = "yonetici@tamir.local";
    private const string RepairDispatcher = "dispecer@tamir.local";
    private const string Office = "teknisyen2@tamir.local";
    private const string Tech = "teknisyen1@tamir.local";

    [Fact]
    public async Task Assignee_sets_stage_after_accepting_and_dispatcher_sees_it()
    {
        var dispatcher = await api.LoginAs(RepairDispatcher);
        var worker = await api.LoginAs(Office);
        var other = await api.LoginAs(Tech);
        var task = await dispatcher.CreateTaskAsync(await NewPaperwork(dispatcher, (await worker.MeAsync()).Id));

        // Kabul edilmeden aşama seçilemez.
        var early = await Assert.ThrowsAsync<ApiException>(() => worker.ChangeStageAsync(task.Id, new("Okunda")));
        Assert.Equal(HttpStatusCode.BadRequest, early.Status);

        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        var updated = await worker.ChangeStageAsync(task.Id, new("Okunda"));
        Assert.Equal("Okunda", updated.Stage);
        await worker.ChangeStageAsync(task.Id, new("Okunda")); // tekrar gönderim idempotent

        var detail = await dispatcher.GetTaskAsync(task.Id);
        Assert.Equal("Okunda", detail.Stage);
        Assert.Single(detail.Events, e => e.Stage == "Okunda");
        Assert.Contains(await dispatcher.GetNotificationsAsync(), n => n.Kind == NotificationKinds.StageChanged && n.WorkTaskId == task.Id);

        var list = await dispatcher.GetTasksAsync(new TaskQuery { TaskTypeId = task.TaskTypeId, Stage = "Okunda" });
        Assert.Contains(list.Items, t => t.Id == task.Id);

        // Tipte olmayan aşama reddedilir; başka çalışan değiştiremez.
        var bad = await Assert.ThrowsAsync<ApiException>(() => worker.ChangeStageAsync(task.Id, new("Uydurma")));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        await Assert.ThrowsAsync<ApiException>(() => other.ChangeStageAsync(task.Id, new("İmzaya çıktı")));

        // Aşama temizlenebilir; yeniden atamada sıfırlanır.
        Assert.Null((await worker.ChangeStageAsync(task.Id, new(null))).Stage);
        await worker.ChangeStageAsync(task.Id, new("İmzaya çıktı"));
        var reassigned = await dispatcher.AssignTaskAsync(task.Id, (await other.MeAsync()).Id);
        Assert.Null(reassigned.Stage);
    }

    [Fact]
    public async Task Desk_task_needs_no_location_and_completes_right_after_accepting()
    {
        var dispatcher = await api.LoginAs(RepairDispatcher);
        var worker = await api.LoginAs(Office);
        var task = await dispatcher.CreateTaskAsync(await NewPaperwork(dispatcher, (await worker.MeAsync()).Id));
        Assert.False(task.HasLocation);

        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        var done = await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Completed });
        Assert.Equal(WorkTaskStatus.Completed, done.Status);
        Assert.NotNull(done.StartedAt);
    }

    [Fact]
    public async Task Field_task_still_requires_location_and_visit_steps()
    {
        var dispatcher = await api.LoginAs(RepairDispatcher);
        var worker = await api.LoginAs(Tech);
        var type = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Montaj");
        Assert.True(type.RequiresVisit);

        var req = new SaveWorkTaskRequest
        {
            TaskTypeId = type.Id, Title = "Konumsuz montaj", AssigneeId = (await worker.MeAsync()).Id,
            CustomFieldValues = new() { ["product"] = "Klima" }
        };
        await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreateTaskAsync(req));

        req.Address = "Şişli";
        req.Latitude = 41.05;
        req.Longitude = 28.99;
        var task = await dispatcher.CreateTaskAsync(req);
        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        var skip = await Assert.ThrowsAsync<ApiException>(() =>
            worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Completed }));
        Assert.Equal(HttpStatusCode.BadRequest, skip.Status);
    }

    [Fact]
    public async Task Task_type_saves_stages_and_rejects_duplicates()
    {
        var admin = await api.LoginAs(RepairAdmin);
        var created = await admin.CreateTaskTypeAsync(new SaveTaskTypeRequest("Sözleşme onayı", null, "description", "#5e35b1", true,
            [], new CompletionRequirements(), ["Taslak hazır", "Hukukta", "Onaylandı"], RequiresVisit: false));
        Assert.Equal(["Taslak hazır", "Hukukta", "Onaylandı"], created.Stages);
        Assert.False(created.RequiresVisit);

        var ex = await Assert.ThrowsAsync<ApiException>(() => admin.CreateTaskTypeAsync(new SaveTaskTypeRequest("Tekrarlı", null, "description",
            "#5e35b1", true, [], new CompletionRequirements(), ["Okunda", "okunda"])));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public void Stage_validation_rules()
    {
        Assert.Empty(TaskRules.ValidateTaskType("Tip", [], ["A", "B"]));
        Assert.NotEmpty(TaskRules.ValidateTaskType("Tip", [], ["A", " "]));
        Assert.NotEmpty(TaskRules.ValidateTaskType("Tip", [], ["Okunda", "OKUNDA"]));
        Assert.NotEmpty(TaskRules.ValidateTaskType("Tip", [], Enumerable.Range(0, TaskRules.MaxStages + 1).Select(i => $"A{i}").ToList()));
        Assert.Equal([WorkTaskStatus.Completed, WorkTaskStatus.Failed, WorkTaskStatus.Cancelled],
            TaskStatusFlow.NextStatuses(WorkTaskStatus.Accepted, requiresVisit: false));
    }

    private static async Task<SaveWorkTaskRequest> NewPaperwork(WfmApiClient dispatcher, Guid assignee)
    {
        var type = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Evrak İşleri");
        return new SaveWorkTaskRequest
        {
            TaskTypeId = type.Id,
            Title = "Ruhsat yenileme dosyası",
            AssigneeId = assignee,
            CustomFieldValues = new() { ["document_no"] = "RH-1" }
        };
    }
}
