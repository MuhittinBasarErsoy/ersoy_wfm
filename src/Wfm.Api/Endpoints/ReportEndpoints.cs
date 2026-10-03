using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reports/summary", async (HttpContext ctx, WfmDbContext db, int? days) =>
        {
            var tid = ctx.User.TenantId();
            var today = DateTime.UtcNow.Date;
            var from = today.AddDays(-(Math.Clamp(days ?? 30, 1, 365) - 1));

            var counts = await db.Tasks.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            foreach (var s in Enum.GetValues<WorkTaskStatus>()) counts.TryAdd(s, 0);

            var recent = await db.Tasks.Where(t => t.CreatedAt >= from || (t.CompletedAt != null && t.CompletedAt >= from))
                .Select(t => new { t.AssigneeId, t.Status, t.CreatedAt, t.StartedAt, t.CompletedAt }).ToListAsync();

            static double? Avg(IEnumerable<double> xs) { var l = xs.ToList(); return l.Count == 0 ? null : Math.Round(l.Average(), 1); }
            static IEnumerable<double> Durations<T>(IEnumerable<T> items, Func<T, DateTime?> start, Func<T, DateTime?> end) =>
                items.Where(i => start(i) != null && end(i) != null).Select(i => (end(i)!.Value - start(i)!.Value).TotalMinutes);

            var completed = recent.Where(t => t.Status == WorkTaskStatus.Completed && t.CompletedAt >= from).ToList();
            var workers = await (from u in db.Users
                                 join ur in db.UserRoles on u.Id equals ur.UserId
                                 join r in db.Roles on ur.RoleId equals r.Id
                                 where u.TenantId == tid && r.Name == Roles.FieldWorker
                                 select new { u.Id, u.FullName }).ToListAsync();
            var open = await db.Tasks.Where(t => t.AssigneeId != null && t.Status != WorkTaskStatus.Completed &&
                                                 t.Status != WorkTaskStatus.Cancelled && t.Status != WorkTaskStatus.Failed)
                .GroupBy(t => t.AssigneeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

            var workerStats = workers.Select(w =>
            {
                var mine = recent.Where(t => t.AssigneeId == w.Id).ToList();
                return new WorkerStatsDto(w.Id, w.FullName,
                    mine.Count(t => t.Status == WorkTaskStatus.Completed),
                    mine.Count(t => t.Status == WorkTaskStatus.Failed),
                    open.GetValueOrDefault(w.Id),
                    Avg(Durations(mine.Where(t => t.Status == WorkTaskStatus.Completed), t => t.StartedAt, t => t.CompletedAt)));
            }).OrderByDescending(w => w.Completed).ToList();

            var last7 = Enumerable.Range(0, 7).Select(i => today.AddDays(-6 + i)).Select(d => new DailyCountDto(
                DateOnly.FromDateTime(d),
                recent.Count(t => t.CreatedAt >= d && t.CreatedAt < d.AddDays(1)),
                recent.Count(t => t.Status == WorkTaskStatus.Completed && t.CompletedAt >= d && t.CompletedAt < d.AddDays(1)))).ToList();

            var activeWorkers = await db.Shifts.Where(s => s.EndedAt == null).Select(s => s.UserId).Distinct().CountAsync();

            return new ReportSummaryDto(counts,
                completed.Count(t => t.CompletedAt >= today),
                recent.Count(t => t.CreatedAt >= today),
                Avg(Durations(completed, t => t.StartedAt, t => t.CompletedAt)),
                activeWorkers, workerStats, last7);
        }).RequireAuthorization(Policies.ViewReports).WithTags("Reports");
    }
}
