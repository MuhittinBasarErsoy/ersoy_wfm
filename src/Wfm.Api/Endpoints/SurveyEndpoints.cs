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
                    TenantId = s.TenantId, SurveyId = s.Id, VersionNo = s.Versions.Select(v => v.VersionNo).DefaultIfEmpty(0).Max() + 1,
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
    }

    private static Task<Survey?> Load(WfmDbContext db, Guid id) =>
        db.Surveys.Include(s => s.Versions).AsSplitQuery().FirstOrDefaultAsync(s => s.Id == id);

    private static SurveyVersion? Current(Survey s) => s.Versions.FirstOrDefault(v => v.Id == s.CurrentVersionId);

    private static bool HasChanges(Survey s, SurveyVersion v) =>
        s.RespondentMode != v.RespondentMode || s.ConsentText != v.ConsentText ||
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
