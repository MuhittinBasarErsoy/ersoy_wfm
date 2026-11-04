using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Endpoints;

/// <summary>Anket tanımları ve yayımlama. Düzenleme taslağı değiştirir; yayımlanan sürümler değişmez.</summary>
public static class SurveyEndpoints
{
    public static void MapSurveyEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/surveys").WithTags("Surveys").RequireAuthorization(Policies.ViewTracking);

        g.MapGet("/", async (SurveyStatus? status, WfmDbContext db) =>
        {
            var q = db.Surveys.Include(s => s.Versions).AsSplitQuery().AsQueryable();
            if (status is { } st) q = q.Where(s => s.Status == st);
            return (await q.OrderBy(s => s.Status).ThenByDescending(s => s.UpdatedAt).ToListAsync()).Select(ToDto).ToList();
        });

        g.MapGet("/{id:guid}", async (Guid id, WfmDbContext db) =>
            await Load(db, id) is { } s ? Results.Ok(ToDto(s)) : Results.NotFound());

        g.MapPost("/", async (SaveSurveyRequest req, HttpContext ctx, WfmDbContext db) =>
        {
            if (Validate(req) is { } error) return Results.BadRequest(new ApiError(error));
            var s = new Survey { TenantId = ctx.User.TenantId(), CreatedById = ctx.User.UserId() };
            Apply(s, req);
            db.Surveys.Add(s);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(s));
        }).RequireAuthorization(Policies.ManageTasks);

        g.MapPut("/{id:guid}", async (Guid id, SaveSurveyRequest req, WfmDbContext db) =>
        {
            var s = await Load(db, id);
            if (s is null) return Results.NotFound();
            if (Validate(req) is { } error) return Results.BadRequest(new ApiError(error));
            Apply(s, req);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(s));
        }).RequireAuthorization(Policies.ManageTasks);

        // Hiç yayımlanmamış taslak silinir; yayımlanmışı görev ve yanıtlar kullanabileceği için arşivlenir.
        g.MapDelete("/{id:guid}", async (Guid id, WfmDbContext db) =>
        {
            var s = await Load(db, id);
            if (s is null) return Results.NotFound();
            if (s.Versions.Count > 0) return Results.BadRequest(new ApiError("Yayımlanmış anket silinemez, arşivleyebilirsiniz."));
            db.Surveys.Remove(s);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManageTasks);

        g.MapPost("/{id:guid}/publish", async (Guid id, HttpContext ctx, WfmDbContext db) =>
        {
            var s = await Load(db, id);
            if (s is null) return Results.NotFound();
            var errors = SurveyRules.ValidateDefinition(s.Questions, s.RespondentMode, s.ConsentText);
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));

            // Taslak yayımlı sürümle aynıysa yeni sürüm açılmaz (arşivden geri almak gibi).
            if (Current(s) is not { } current || HasChanges(s, current))
            {
                var version = new SurveyVersion
                {
                    TenantId = s.TenantId, SurveyId = s.Id, VersionNo = s.Versions.Select(v => v.VersionNo).DefaultIfEmpty(0).Max() + 1, Title = s.Title,
                    Questions = Copy(s.Questions), RespondentMode = s.RespondentMode, ConsentText = s.ConsentText,
                    PublishedById = ctx.User.UserId()
                };
                db.SurveyVersions.Add(version);
                s.Versions.Add(version);
                s.CurrentVersionId = version.Id;
            }
            s.Status = SurveyStatus.Published;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(s));
        }).RequireAuthorization(Policies.ManageTasks);

        // Arşivlenen anket yeni görevlere bağlanamaz; açık görevler kendi sürümleriyle devam eder.
        g.MapPost("/{id:guid}/archive", async (Guid id, WfmDbContext db) =>
        {
            var s = await Load(db, id);
            if (s is null) return Results.NotFound();
            if (s.Versions.Count == 0) return Results.BadRequest(new ApiError("Yayımlanmamış taslak arşivlenemez, silebilirsiniz."));
            s.Status = SurveyStatus.Archived;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(s));
        }).RequireAuthorization(Policies.ManageTasks);

        // ---------- Sahadan yanıt ----------
        app.MapPost("/api/tasks/{id:guid}/survey-responses", async (Guid id, SubmitSurveyResponseRequest req, HttpContext ctx, WfmDbContext db) =>
        {
            var uid = ctx.User.UserId();
            if (req.ClientId == Guid.Empty) return Results.BadRequest(new ApiError("İstemci kimliği eksik."));
            var now = DateTime.UtcNow;
            // İnternetsiz doldurulan ankette an cihazdan gelir; gelecekteki zamanlara izin verilmez.
            var submittedAt = req.SubmittedAt != default && req.SubmittedAt.ToUniversalTime() < now ? req.SubmittedAt.ToUniversalTime() : now;

            for (var attempt = 0; ; attempt++)
            {
                var task = await db.Tasks.Include(t => t.TaskType).Include(t => t.SurveyVersion).FirstOrDefaultAsync(t => t.Id == id);
                if (task is null) return Results.NotFound();
                if (task.AssigneeId != uid) return Results.Json(new ApiError("Bu görev size atanmamış."), statusCode: 403);
                if (task.SurveyVersion is not { } version) return Results.BadRequest(new ApiError("Bu görevde anket yok."));

                // Tekrar gönderim: aynı yanıt zaten kayıtlıysa başarı dön (idempotent).
                if (await db.SurveyResponses.Where(r => r.WorkTaskId == id && r.ClientId == req.ClientId).Select(r => (Guid?)r.Id).FirstOrDefaultAsync() is { } existing)
                    return Results.Ok(new SurveyResponseResultDto(existing, task.SurveyResponseCount, task.SurveyTargetCount));

                // Kapanış sonrası gelen yanıt, cihazda kapanıştan önce doldurulduysa kabul edilir: internetsiz veri kaybolmasın.
                if (!task.AcceptsSurveyResponses)
                {
                    var closedAt = task.IsOpen ? (DateTime?)null : task.CompletedAt ?? task.UpdatedAt;
                    if (closedAt is null) return Results.BadRequest(new ApiError("Anket için önce görev yerine varın."));
                    if (submittedAt > closedAt) return Results.BadRequest(new ApiError("Görev kapandı, anket eklenemez."));
                }

                var errors = SurveyRules.ValidateAnswers(version.Questions, version.RespondentMode, req.Answers, req.RespondentName, req.RespondentPhone, req.ConsentGiven);
                if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));

                var anonymous = version.RespondentMode == SurveyRespondentMode.None;
                var response = new SurveyResponse
                {
                    TenantId = task.TenantId, SurveyId = version.SurveyId, SurveyVersionId = version.Id, WorkTaskId = id, UserId = uid,
                    StartedAt = req.StartedAt == default ? submittedAt : req.StartedAt.ToUniversalTime(),
                    SubmittedAt = submittedAt,
                    Latitude = req.Latitude, Longitude = req.Longitude,
                    RespondentName = anonymous ? null : Clip(req.RespondentName, 200),
                    RespondentPhone = anonymous ? null : Clip(req.RespondentPhone, 50),
                    ConsentGiven = !anonymous && req.ConsentGiven,
                    Answers = SurveyRules.VisibleAnswers(version.Questions, req.Answers),
                    ClientId = req.ClientId
                };
                db.SurveyResponses.Add(response);
                task.SurveyResponseCount++;
                try
                {
                    await db.SaveChangesAsync();
                    return Results.Ok(new SurveyResponseResultDto(response.Id, task.SurveyResponseCount, task.SurveyTargetCount));
                }
                catch (DbUpdateException) when (attempt < 3)
                {
                    // Görev aynı anda güncellendi ya da aynı yanıt iki kez geldi: güncel hâliyle tekrar dene.
                    db.ChangeTracker.Clear();
                }
            }
        }).RequireAuthorization(Policies.FieldWork);
    }

    private static string? Clip(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }

    /// <summary>Görev formundaki anket seçimini uygular. Görev mevcut sürümünde kalır; ilk yanıttan sonra anket değiştirilemez.</summary>
    public static async Task<string?> ApplyToTaskAsync(WorkTask task, SaveWorkTaskRequest req, WfmDbContext db)
    {
        if (req.SurveyId == task.SurveyId)
        {
            if (task.SurveyId is null) return null;
        }
        else
        {
            if (task.SurveyResponseCount > 0) return "Yanıt alınmış görevin anketi değiştirilemez.";
            if (req.SurveyId is not { } surveyId)
            {
                task.SurveyId = task.SurveyVersionId = null;
                task.SurveyVersion = null;
                task.SurveyTargetCount = 0;
                return null;
            }
            var survey = await db.Surveys.FirstOrDefaultAsync(s => s.Id == surveyId);
            if (survey is null) return "Anket bulunamadı.";
            if (survey.Status != SurveyStatus.Published || survey.CurrentVersionId is null) return "Yalnızca yayımlanmış anket göreve bağlanabilir.";
            task.SurveyId = survey.Id;
            task.SurveyVersionId = survey.CurrentVersionId;
        }
        if (req.SurveyTargetCount is < 1 or > 100_000) return "Hedef anket sayısı 1 ile 100.000 arasında olmalı.";
        task.SurveyTargetCount = req.SurveyTargetCount;
        return null;
    }

    private static Task<Survey?> Load(WfmDbContext db, Guid id) =>
        db.Surveys.Include(s => s.Versions).AsSplitQuery().FirstOrDefaultAsync(s => s.Id == id);

    private static SurveyVersion? Current(Survey s) => s.Versions.FirstOrDefault(v => v.Id == s.CurrentVersionId);

    private static bool HasChanges(Survey s, SurveyVersion v) =>
        s.Title != v.Title || s.RespondentMode != v.RespondentMode || s.ConsentText != v.ConsentText ||
        JsonSerializer.Serialize(s.Questions.OrderBy(q => q.Order)) != JsonSerializer.Serialize(v.Questions.OrderBy(q => q.Order));

    private static List<SurveyQuestion> Copy(List<SurveyQuestion> questions) =>
        JsonSerializer.Deserialize<List<SurveyQuestion>>(JsonSerializer.Serialize(questions))!;

    /// <summary>Taslak yarım kalabilir; sıkı kurallar yayımlarken uygulanır (<see cref="SurveyRules.ValidateDefinition"/>).</summary>
    private static string? Validate(SaveSurveyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return "Anket adı zorunlu.";
        if (req.Title.Length > 200) return "Anket adı en fazla 200 karakter olabilir.";
        if (req.Questions.Count > SurveyRules.MaxQuestions) return $"En fazla {SurveyRules.MaxQuestions} soru eklenebilir.";
        return null;
    }

    private static void Apply(Survey s, SaveSurveyRequest req)
    {
        s.Title = req.Title.Trim();
        s.Description = req.Description;
        s.RespondentMode = req.RespondentMode;
        s.ConsentText = string.IsNullOrWhiteSpace(req.ConsentText) ? null : req.ConsentText.Trim();
        s.Questions = req.Questions.Select((q, i) => new SurveyQuestion
        {
            Id = q.Id == Guid.Empty ? Guid.NewGuid() : q.Id,
            Order = i + 1,
            Type = q.Type,
            Text = q.Text.Trim(),
            HelpText = string.IsNullOrWhiteSpace(q.HelpText) ? null : q.HelpText.Trim(),
            Required = q.Required,
            Options = SurveyRules.HasOptions(q.Type) ? q.Options.Select(o => o.Trim()).Where(o => o.Length > 0).ToList() : [],
            ShowIf = q.ShowIf is { QuestionId: var parent, Value: var value } && parent != Guid.Empty && !string.IsNullOrEmpty(value)
                ? new SurveyCondition { QuestionId = parent, Value = value } : null
        }).ToList();
    }

    private static SurveyDto ToDto(Survey s)
    {
        var current = Current(s);
        return new SurveyDto
        {
            Id = s.Id,
            Title = s.Title,
            Description = s.Description,
            Status = s.Status,
            RespondentMode = s.RespondentMode,
            ConsentText = s.ConsentText,
            Questions = s.Questions.OrderBy(q => q.Order).ToList(),
            CurrentVersionId = s.CurrentVersionId,
            CurrentVersionNo = current?.VersionNo,
            PublishedAt = current?.PublishedAt,
            HasUnpublishedChanges = current is not null && HasChanges(s, current),
            UpdatedAt = s.UpdatedAt
        };
    }
}
