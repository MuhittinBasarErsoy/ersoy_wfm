using Microsoft.AspNetCore.Mvc;
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
        app.MapGet("/api/reports/summary", async (HttpContext ctx, WfmDbContext db, int? days,
            [FromQuery(Name = "from")] DateOnly? fromDate, [FromQuery(Name = "to")] DateOnly? toDate) =>
        {
            var tid = ctx.User.TenantId();
            var now = DateTime.UtcNow;
            var today = now.Date;
            // Serbest aralık (from/to, en fazla 366 gün) verilmişse o, yoksa "son N gün".
            DateTime start, end;
            if (fromDate is { } f && toDate is { } t2 && f <= t2)
            {
                start = f.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                end = t2.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1);
                if ((end - start).TotalDays > 366) start = end.AddDays(-366);
            }
            else
            {
                end = today.AddDays(1);
                start = today.AddDays(-(Math.Clamp(days ?? 30, 1, 365) - 1));
            }
            var period = (int)(end - start).TotalDays;
            var from = start;

            var counts = await db.Tasks.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            foreach (var s in Enum.GetValues<WorkTaskStatus>()) counts.TryAdd(s, 0);

            // Son 7 gün grafiği dönemden bağımsız olduğu için en az 7 günlük veri çekilir; dönem hesapları ayrıca süzülür.
            var since = from < today.AddDays(-6) ? from : today.AddDays(-6);
            var fetched = await db.Tasks.Where(t => t.CreatedAt >= since || (t.CompletedAt != null && t.CompletedAt >= since))
                .Select(t => new { t.AssigneeId, t.TaskTypeId, t.Status, t.CreatedAt, t.StartedAt, t.CompletedAt }).ToListAsync();
            var recent = fetched.Where(t => (t.CreatedAt >= from && t.CreatedAt < end) || (t.CompletedAt >= from && t.CompletedAt < end)).ToList();

            static double? Avg(IEnumerable<double> xs) { var l = xs.ToList(); return l.Count == 0 ? null : Math.Round(l.Average(), 1); }
            static IEnumerable<double> Durations<T>(IEnumerable<T> items, Func<T, DateTime?> start, Func<T, DateTime?> end) =>
                items.Where(i => start(i) != null && end(i) != null).Select(i => (end(i)!.Value - start(i)!.Value).TotalMinutes);

            var completed = recent.Where(t => t.Status == WorkTaskStatus.Completed && t.CompletedAt >= from && t.CompletedAt < end).ToList();
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

            DailyCountDto Day(DateTime d) => new(DateOnly.FromDateTime(d),
                fetched.Count(t => t.CreatedAt >= d && t.CreatedAt < d.AddDays(1)),
                fetched.Count(t => t.Status == WorkTaskStatus.Completed && t.CompletedAt >= d && t.CompletedAt < d.AddDays(1)));
            var last7 = Enumerable.Range(0, 7).Select(i => Day(today.AddDays(-6 + i))).ToList();
            var daily = Enumerable.Range(0, period).Select(i => Day(from.AddDays(i))).ToList();

            var typeNames = await db.TaskTypes.Select(t => new { t.Id, t.Name, t.Color }).ToListAsync();
            var types = typeNames.Select(tt =>
            {
                var mine = recent.Where(t => t.TaskTypeId == tt.Id).ToList();
                return new TypeStatsDto(tt.Id, tt.Name, tt.Color,
                    mine.Count(t => t.CreatedAt >= from && t.CreatedAt < end),
                    mine.Count(t => t.Status == WorkTaskStatus.Completed && t.CompletedAt >= from && t.CompletedAt < end),
                    mine.Count(t => t.Status == WorkTaskStatus.Failed && t.CompletedAt >= from && t.CompletedAt < end),
                    Avg(Durations(mine.Where(t => t.Status == WorkTaskStatus.Completed), t => t.StartedAt, t => t.CompletedAt)));
            }).Where(t => t.Created + t.Completed + t.Failed > 0).OrderByDescending(t => t.Created).ToList();

            var overdue = await db.Tasks.CountAsync(t => t.ScheduledEnd != null && t.ScheduledEnd < now &&
                t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled && t.Status != WorkTaskStatus.Failed);

            var activeWorkers = await db.Shifts.Where(s => s.EndedAt == null).Select(s => s.UserId).Distinct().CountAsync();

            return new ReportSummaryDto(counts,
                completed.Count(t => t.CompletedAt >= today),
                recent.Count(t => t.CreatedAt >= today),
                Avg(Durations(completed, t => t.StartedAt, t => t.CompletedAt)),
                activeWorkers, workerStats, last7,
                overdue, counts.GetValueOrDefault(WorkTaskStatus.Draft) + counts.GetValueOrDefault(WorkTaskStatus.Rejected),
                daily, types);
        }).RequireAuthorization(Policies.ViewReports).WithTags("Reports");
    }
}
