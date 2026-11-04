using Wfm.Domain.Common;
using Wfm.Domain.Enums;

namespace Wfm.Domain.Entities;

/// <summary>
/// Şirketin tanımladığı anket. <see cref="Questions"/> düzenlenen taslaktır; yayımlanınca değişmez bir
/// <see cref="SurveyVersion"/> kopyası oluşur. Görevler ve yanıtlar sürüme bağlanır, anket sonradan değişse de bozulmaz.
/// </summary>
public class Survey : TenantEntity
{
    public const string DefaultConsentText =
        "Adımın ve telefon numaramın bu anket kapsamında işlenmesini ve benimle iletişim kurulmasını kabul ediyorum.";

    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public SurveyStatus Status { get; set; } = SurveyStatus.Draft;
    public SurveyRespondentMode RespondentMode { get; set; } = SurveyRespondentMode.None;
    public string? ConsentText { get; set; }
    public List<SurveyQuestion> Questions { get; set; } = [];
    public Guid CreatedById { get; set; }
    /// <summary>Yeni görevlerin bağlanacağı sürüm; hiç yayımlanmadıysa boş.</summary>
    public Guid? CurrentVersionId { get; set; }

    public List<SurveyVersion> Versions { get; set; } = [];
}

/// <summary>Anketin yayımlanmış, değişmez hâli.</summary>
public class SurveyVersion : TenantEntity
{
    public Guid SurveyId { get; set; }
    public int VersionNo { get; set; }
    public string Title { get; set; } = "";
    public List<SurveyQuestion> Questions { get; set; } = [];
    public SurveyRespondentMode RespondentMode { get; set; }
    public string? ConsentText { get; set; }
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
    public Guid PublishedById { get; set; }
}

public class SurveyQuestion
{
    /// <summary>Sürümler arasında korunur; rapor aynı soruyu bu kimlikle birleştirir.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public SurveyQuestionType Type { get; set; }
    public string Text { get; set; } = "";
    public string? HelpText { get; set; }
    public bool Required { get; set; }
    /// <summary>Seçimli sorularda seçenekler.</summary>
    public List<string> Options { get; set; } = [];
    /// <summary>Doluysa soru yalnızca koşul sağlanınca sorulur.</summary>
    public SurveyCondition? ShowIf { get; set; }
}

/// <summary>"<see cref="QuestionId"/> sorusunun cevabı <see cref="Value"/> ise göster."</summary>
public class SurveyCondition
{
    public Guid QuestionId { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>Sahada doldurulan bir anket. Sürüme bağlıdır; anket sonradan değişse de cevaplar anlamını korur.</summary>
public class SurveyResponse : TenantEntity
{
    public Guid SurveyId { get; set; }
    public Guid SurveyVersionId { get; set; }
    public Guid WorkTaskId { get; set; }
    /// <summary>Anketi yapan çalışan.</summary>
    public Guid UserId { get; set; }
    public DateTime StartedAt { get; set; }
    /// <summary>Cihazda gönderildiği an (internetsiz yanıtlarda sunucuya sonra gelir).</summary>
    public DateTime SubmittedAt { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? RespondentName { get; set; }
    public string? RespondentPhone { get; set; }
    public bool ConsentGiven { get; set; }
    public List<SurveyAnswer> Answers { get; set; } = [];
    /// <summary>Tekrar gönderimlerde çift kaydı önlemek için istemcinin ürettiği kimlik.</summary>
    public Guid ClientId { get; set; }
}

/// <summary>
/// Bir sorunun cevabı. <see cref="Values"/> yalnızca çoklu seçimde kullanılır; diğerlerinde <see cref="Value"/>:
/// seçenek metni, Evet/Hayır, puan, sayı (noktalı), tarih (yyyy-MM-dd), konum ("enlem,boylam"), fotoğraf (ekin istemci kimliği).
/// </summary>
public class SurveyAnswer
{
    public Guid QuestionId { get; set; }
    public string? Value { get; set; }
    public List<string> Values { get; set; } = [];
}
