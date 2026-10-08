using Wfm.Domain.Common;
using Wfm.Domain.Enums;

namespace Wfm.Domain.Entities;

/// <summary>
/// Birden fazla görevden (adım) oluşan iş. Adımlar "sıra" numarasıyla gruplanır: aynı sıradakiler paralel yürür,
/// bir sıranın tüm adımları bitince sonraki sıra başlar. Her adım normal bir <see cref="WorkTask"/>'tır.
/// </summary>
public class Job : TenantEntity
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Active;
    /// <summary>Şu an yürüyen sıra numarası; 0 = henüz başlamadı (ilk <see cref="Advance"/> ilk sırayı açar).</summary>
    public int CurrentOrder { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string Address { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<WorkTask> Tasks { get; set; } = [];

    public bool IsOpen => Status is JobStatus.Active or JobStatus.OnHold;

    /// <summary>Akıştaki sıra sayısı. Yerine yenisi açılan (StepOrder = null) eski denemeler sayılmaz.</summary>
    public int OrderCount => Tasks.Select(t => t.StepOrder ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Adımların durumuna göre işi ilerletir. Aktive edilmesi gereken (sırası gelen) adımları döner;
    /// iş beklemeye alındıysa veya tamamlandıysa <see cref="Status"/> buna göre güncellenir.
    /// Tekrar çağrılması güvenlidir: zaten aktif adımlar yeniden dönmez.
    /// </summary>
    public JobAdvance Advance()
    {
        if (!IsOpen) return new JobAdvance([], false, false);
        if (Tasks.All(t => t.StepOrder is null)) return new JobAdvance([], false, false);

        var current = Tasks.Where(t => t.StepOrder == CurrentOrder).ToList();
        if (current.Any(t => t.Status is WorkTaskStatus.Failed or WorkTaskStatus.Rejected))
        {
            var wasOnHold = Status == JobStatus.OnHold;
            Status = JobStatus.OnHold;
            return new JobAdvance([], !wasOnHold, false);
        }

        if (Status == JobStatus.OnHold) Status = JobStatus.Active; // sorunlu adım yeniden atandı / atlandı

        // Sıra bitene kadar ilerle; tüm adımları önceden iptal edilmiş sıralar atlanır.
        var orderChanged = false;
        while (current.Count == 0 || !current.Any(t => t.IsOpen))
        {
            var next = Tasks.Where(t => t.StepOrder > CurrentOrder).Select(t => t.StepOrder!.Value).DefaultIfEmpty(0).Min();
            if (next == 0)
            {
                Status = JobStatus.Completed;
                CompletedAt = UpdatedAt = DateTime.UtcNow;
                return new JobAdvance([], false, true, true);
            }
            CurrentOrder = next;
            UpdatedAt = DateTime.UtcNow;
            orderChanged = true;
            current = Tasks.Where(t => t.StepOrder == next).ToList();
        }
        // Yürüyen sırada henüz kimseye verilmemiş adımlar (yeni açılan sıra veya sonradan eklenen paralel adım).
        var activate = current.Where(t => t.Status == WorkTaskStatus.Draft && t.AssigneeId is null).ToList();
        return new JobAdvance(activate, false, false, orderChanged);
    }

    /// <summary>Adım henüz sırası gelmemiş (bekleyen) bir adım mı?</summary>
    public bool IsPending(WorkTask t) => t.StepOrder > CurrentOrder && t.Status == WorkTaskStatus.Draft;
}

/// <param name="Activate">Yürüyen sırada atanmayı bekleyen adımlar (planlanan kişi varsa atanmalı).</param>
/// <param name="PutOnHold">İş bu çağrıda beklemeye alındı.</param>
/// <param name="Completed">İş bu çağrıda tamamlandı.</param>
/// <param name="OrderChanged">Yeni bir sıraya geçildi.</param>
public record JobAdvance(List<WorkTask> Activate, bool PutOnHold, bool Completed, bool OrderChanged = false);

/// <summary>Tekrar kullanılabilir iş akışı (ör. "Evrak gönderimi": Fotokopi → Postane → Mail).</summary>
public class JobTemplate : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<JobTemplateStep> Steps { get; set; } = [];
}

public class JobTemplateStep
{
    /// <summary>Sıra numarası (1'den başlar); aynı sıradaki adımlar paralel yürür.</summary>
    public int Order { get; set; } = 1;
    public Guid TaskTypeId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public Guid? DefaultAssigneeId { get; set; }
}
