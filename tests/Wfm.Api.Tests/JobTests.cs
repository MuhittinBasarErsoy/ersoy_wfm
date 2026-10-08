using System.Net;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Api.Tests;

/// <summary>Birden fazla görevden (adım) oluşan, sıralı/paralel yürüyen işler.</summary>
public class JobTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Dispatcher = "dispecer@tamir.local";
    private const string Office = "teknisyen2@tamir.local"; // Selin
    private const string Tech = "teknisyen1@tamir.local";   // Burak

    [Fact]
    public async Task Sequential_job_hands_steps_over_one_by_one()
    {
        var (dispatcher, office, tech, selin, burak, paper) = await Setup();
        var job = await dispatcher.CreateJobAsync(NewJob("Evrak gönder",
            Step(1, paper, "Fotokopi çek", selin), Step(2, paper, "Postaneye götür", burak), Step(3, paper, "Mail at", selin)));

        Assert.Equal(1, job.CurrentOrder);
        Assert.Equal(3, job.OrderCount);
        var (copy, post, mail) = (job.Steps[0], job.Steps[1], job.Steps[2]);
        Assert.Equal(WorkTaskStatus.Assigned, copy.Status);
        Assert.Equal(WorkTaskStatus.Draft, post.Status);
        Assert.Null(post.AssigneeId);
        Assert.Equal(burak, post.PlannedAssigneeId);

        // Burak'a sırası gelmeden görev görünmez.
        Assert.DoesNotContain((await tech.SyncAsync()).Tasks, t => t.JobId == job.Id);

        await Complete(office, copy.Id, "Fotokopiler masada");
        await Complete(office, copy.Id, "Fotokopiler masada"); // offline tekrar gönderim

        var afterFirst = await dispatcher.GetJobAsync(job.Id);
        Assert.Equal(2, afterFirst.CurrentOrder);
        var postTask = Assert.Single((await tech.SyncAsync()).Tasks, t => t.JobId == job.Id);
        Assert.Equal(WorkTaskStatus.Assigned, postTask.Status);
        Assert.Equal(2, postTask.Job!.StepOrder);
        Assert.Equal("Fotokopiler masada", Assert.Single(postTask.Job.PreviousSteps).Note);
        Assert.Single(await tech.GetNotificationsAsync(), n => n.WorkTaskId == post.Id && n.Kind == NotificationKinds.Assigned);
        Assert.Equal(WorkTaskStatus.Draft, (await dispatcher.GetTaskAsync(mail.Id)).Status);

        await Complete(tech, post.Id);
        await Complete(office, mail.Id);

        var done = await dispatcher.GetJobAsync(job.Id);
        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.Equal(3, done.DoneSteps);
        Assert.Contains(await dispatcher.GetNotificationsAsync(), n => n.Kind == NotificationKinds.JobCompleted && n.Body == "Evrak gönder");
    }

    [Fact]
    public async Task Parallel_steps_must_all_finish_before_next_order()
    {
        var (dispatcher, office, tech, selin, burak, paper) = await Setup();
        var job = await dispatcher.CreateJobAsync(NewJob("Paralel iş",
            Step(1, paper, "Dosya A", selin), Step(1, paper, "Dosya B", burak), Step(2, paper, "Birleştir", selin)));

        Assert.All(job.Steps.Where(s => s.StepOrder == 1), s => Assert.Equal(WorkTaskStatus.Assigned, s.Status));
        await Complete(office, job.Steps[0].Id);
        Assert.Equal(1, (await dispatcher.GetJobAsync(job.Id)).CurrentOrder);
        Assert.Equal(WorkTaskStatus.Draft, (await dispatcher.GetTaskAsync(job.Steps[2].Id)).Status);

        await Complete(tech, job.Steps[1].Id);
        var next = await dispatcher.GetJobAsync(job.Id);
        Assert.Equal(2, next.CurrentOrder);
        Assert.Equal(WorkTaskStatus.Assigned, next.Steps.Single(s => s.StepOrder == 2).Status);
    }

    [Fact]
    public async Task Failed_step_puts_job_on_hold_until_retried()
    {
        var (dispatcher, office, tech, selin, burak, paper) = await Setup();
        var job = await dispatcher.CreateJobAsync(NewJob("Sorunlu iş", Step(1, paper, "Postaneye götür", burak), Step(2, paper, "Mail at", selin)));
        var first = job.Steps[0];

        await tech.ChangeStatusAsync(first.Id, new() { Status = WorkTaskStatus.Accepted });
        await tech.ChangeStatusAsync(first.Id, new() { Status = WorkTaskStatus.Failed, Note = "Postane kapalı" });

        var held = await dispatcher.GetJobAsync(job.Id);
        Assert.Equal(JobStatus.OnHold, held.Status);
        Assert.Equal(WorkTaskStatus.Draft, held.Steps.Single(s => s.StepOrder == 2).Status);
        Assert.Contains(await dispatcher.GetNotificationsAsync(), n => n.Kind == NotificationKinds.JobOnHold && n.WorkTaskId == first.Id);

        // Yeni deneme Selin'e verilir; eski deneme geçmişte kalır.
        var retried = await dispatcher.RetryJobStepAsync(job.Id, first.Id, selin);
        Assert.Equal(JobStatus.Active, retried.Status);
        var retry = retried.Steps.Single(s => s.StepOrder == 1);
        Assert.NotEqual(first.Id, retry.Id);
        Assert.Equal(selin, retry.AssigneeId);
        Assert.Contains(retried.Steps, s => s.Id == first.Id && s.StepOrder == null);

        await Complete(office, retry.Id);
        Assert.Equal(2, (await dispatcher.GetJobAsync(job.Id)).CurrentOrder);
    }

    [Fact]
    public async Task Rejected_step_continues_after_reassign()
    {
        var (dispatcher, office, tech, selin, burak, paper) = await Setup();
        var job = await dispatcher.CreateJobAsync(NewJob("Reddedilen", Step(1, paper, "Fotokopi", burak), Step(2, paper, "Mail", selin)));
        await tech.ChangeStatusAsync(job.Steps[0].Id, new() { Status = WorkTaskStatus.Rejected, Note = "Yoğunum" });
        Assert.Equal(JobStatus.OnHold, (await dispatcher.GetJobAsync(job.Id)).Status);

        await dispatcher.AssignTaskAsync(job.Steps[0].Id, selin);
        Assert.Equal(JobStatus.Active, (await dispatcher.GetJobAsync(job.Id)).Status);
        await Complete(office, job.Steps[0].Id);
        Assert.Equal(2, (await dispatcher.GetJobAsync(job.Id)).CurrentOrder);
    }

    [Fact]
    public async Task Pending_step_assignment_only_changes_planned_person_and_steps_can_be_edited()
    {
        var (dispatcher, office, _, selin, burak, paper) = await Setup();
        var job = await dispatcher.CreateJobAsync(NewJob("Düzenlenen", Step(1, paper, "Fotokopi", selin), Step(2, paper, "Postane", burak)));

        var pending = await dispatcher.AssignTaskAsync(job.Steps[1].Id, selin);
        Assert.Equal(WorkTaskStatus.Draft, pending.Status);
        Assert.Null(pending.AssigneeId);
        Assert.Equal(selin, pending.PlannedAssigneeId);

        // Yürüyen sıraya paralel adım ekle, bekleyen adımı sil, yeni sıra ekle.
        var steps = new List<JobStepInput>
        {
            new() { TaskId = job.Steps[0].Id, Order = 1, TaskTypeId = paper, Title = "Fotokopi", AssigneeId = selin },
            Step(1, paper, "Zarfla", burak),
            Step(5, paper, "Mail at", selin),
        };
        var edited = await dispatcher.UpdateJobStepsAsync(job.Id, steps);
        Assert.Equal(3, edited.StepCount);
        Assert.Equal(2, edited.OrderCount); // 5 → 2 olarak sıkıştırılır
        Assert.Equal(WorkTaskStatus.Assigned, edited.Steps.Single(s => s.Title == "Zarfla").Status);
        Assert.DoesNotContain(edited.Steps, s => s.Id == job.Steps[1].Id);

        // Başlamış adım öne alınamaz.
        var bad = await Assert.ThrowsAsync<ApiException>(() => dispatcher.UpdateJobStepsAsync(job.Id, [Step(0, paper, "X", selin)]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);

        var cancelled = await dispatcher.CancelJobAsync(job.Id);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.All(cancelled.Steps, s => Assert.Equal(WorkTaskStatus.Cancelled, s.Status));
    }

    [Fact]
    public async Task Template_is_saved_and_seeded()
    {
        var (dispatcher, _, _, selin, burak, paper) = await Setup();
        var seeded = Assert.Single(await dispatcher.GetJobTemplatesAsync(), t => t.Name == "Evrak gönderimi");
        Assert.Equal([1, 2, 3], seeded.Steps.Select(s => s.Order));

        var saved = await dispatcher.CreateJobTemplateAsync(new SaveJobTemplateRequest("Paralel şablon", null,
            [Step(3, paper, "Son", selin), Step(1, paper, "A", selin), Step(1, paper, "B", burak)]));
        Assert.Equal([1, 1, 2], saved.Steps.Select(s => s.Order));

        var empty = await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreateJobTemplateAsync(new SaveJobTemplateRequest("Boş", null, [])));
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);

        var worker = await api.LoginAs(Office);
        await Assert.ThrowsAsync<ApiException>(() => worker.GetJobsAsync());
    }

    [Fact]
    public void Advance_skips_fully_cancelled_orders_and_completes()
    {
        var job = new Job();
        WorkTask Make(int order, WorkTaskStatus s) => new() { StepOrder = order, Status = s };
        job.Tasks.AddRange([Make(1, WorkTaskStatus.Completed), Make(2, WorkTaskStatus.Cancelled), Make(3, WorkTaskStatus.Draft)]);
        job.CurrentOrder = 1;
        var r = job.Advance();
        Assert.Equal(3, job.CurrentOrder);
        Assert.Single(r.Activate);
        Assert.True(r.OrderChanged);

        job.Tasks[2].Status = WorkTaskStatus.Completed;
        Assert.True(job.Advance().Completed);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.False(job.Advance().Completed); // tekrar çağrı etkisiz
    }

    private async Task<(WfmApiClient dispatcher, WfmApiClient office, WfmApiClient tech, Guid selin, Guid burak, Guid paper)> Setup()
    {
        var dispatcher = await api.LoginAs(Dispatcher);
        var office = await api.LoginAs(Office);
        var tech = await api.LoginAs(Tech);
        var paper = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Evrak İşleri").Id;
        return (dispatcher, office, tech, (await office.MeAsync()).Id, (await tech.MeAsync()).Id, paper);
    }

    private static JobStepInput Step(int order, Guid type, string title, Guid assignee) =>
        new() { Order = order, TaskTypeId = type, Title = title, AssigneeId = assignee };

    private static SaveJobRequest NewJob(string title, params JobStepInput[] steps) => new() { Title = title, Steps = [.. steps] };

    private static async Task Complete(WfmApiClient worker, Guid taskId, string? note = null)
    {
        var task = await worker.GetTaskAsync(taskId);
        if (task.Status == WorkTaskStatus.Assigned)
            await worker.ChangeStatusAsync(taskId, new() { Status = WorkTaskStatus.Accepted });
        await worker.ChangeStatusAsync(taskId, new() { Status = WorkTaskStatus.Completed, Note = note });
    }
}
