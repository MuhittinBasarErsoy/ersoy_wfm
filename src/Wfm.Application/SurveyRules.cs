using System.Globalization;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Application;

/// <summary>Anket tanımı ve cevap kuralları. API ve istemci (internetsiz doldurma dahil) aynı kuralları kullanır.</summary>
public static class SurveyRules
{
    public const string Yes = "Evet";
    public const string No = "Hayır";
    public const int MaxQuestions = 200;
    public const int MaxTextLength = 500;
    public const int MaxAnswerLength = 4000;

    public static bool HasOptions(SurveyQuestionType t) => t is SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice;

    public static bool CanBeCondition(SurveyQuestionType t) =>
        HasOptions(t) || t is SurveyQuestionType.YesNo or SurveyQuestionType.Rating5 or SurveyQuestionType.Nps10;

    /// <summary>Bir soruya bağlanan koşulda seçilebilecek cevaplar.</summary>
    public static IReadOnlyList<string> ConditionValues(SurveyQuestion q) => q.Type switch
    {
        SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice => q.Options,
        SurveyQuestionType.YesNo => [Yes, No],
        SurveyQuestionType.Rating5 => Enumerable.Range(1, 5).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList(),
        SurveyQuestionType.Nps10 => Enumerable.Range(0, 11).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList(),
        _ => []
    };

    /// <summary>Yayımlamadan önce anket tanımını doğrular.</summary>
    public static List<string> ValidateDefinition(IReadOnlyList<SurveyQuestion> questions, SurveyRespondentMode mode = SurveyRespondentMode.None, string? consentText = null)
    {
        var errors = new List<string>();
        if (questions.Count == 0) errors.Add("En az bir soru ekleyin.");
        if (questions.Count > MaxQuestions) errors.Add($"En fazla {MaxQuestions} soru eklenebilir.");
        if (mode != SurveyRespondentMode.None && string.IsNullOrWhiteSpace(consentText))
            errors.Add("Kişi bilgisi toplanacaksa onay metni zorunlu.");
        if (questions.Select(q => q.Id).Distinct().Count() != questions.Count) errors.Add("Soru kimlikleri tekil olmalı.");

        var ordered = questions.OrderBy(q => q.Order).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var q = ordered[i];
            var name = $"{i + 1}. soru";
            if (string.IsNullOrWhiteSpace(q.Text)) errors.Add($"{name}: metin boş olamaz.");
            else if (q.Text.Length > MaxTextLength) errors.Add($"{name}: metin en fazla {MaxTextLength} karakter olabilir.");

            if (HasOptions(q.Type))
            {
                if (q.Options.Count(o => !string.IsNullOrWhiteSpace(o)) < 2) errors.Add($"{name}: en az iki seçenek girin.");
                if (q.Options.Any(string.IsNullOrWhiteSpace)) errors.Add($"{name}: seçenek boş olamaz.");
                if (q.Options.GroupBy(o => o.Trim(), StringComparer.CurrentCultureIgnoreCase).Any(g => g.Count() > 1))
                    errors.Add($"{name}: aynı seçenek iki kez yazılmış.");
            }

            if (q.ShowIf is not { } c) continue;
            // Yalnızca önceki sorulara bağlanabilir: döngü oluşamaz ve soru sırası anlamlı kalır.
            var parent = ordered.Take(i).FirstOrDefault(p => p.Id == c.QuestionId);
            if (parent is null) errors.Add($"{name}: koşul yalnızca kendinden önceki bir soruya bağlanabilir.");
            else if (!CanBeCondition(parent.Type)) errors.Add($"{name}: koşul seçimli, evet/hayır ya da puan sorusuna bağlanabilir.");
            else if (!ConditionValues(parent).Contains(c.Value)) errors.Add($"{name}: koşuldaki cevap ({c.Value}) bağlı soruda artık yok.");
        }
        return errors;
    }

    public static bool HasValue(SurveyAnswer? a) =>
        a is not null && (!string.IsNullOrWhiteSpace(a.Value) || a.Values.Count > 0);

    /// <summary>Verilen cevaplara göre sorulacak sorular, sırasıyla. Gizli bir soruya bağlı soru da gizli kalır.</summary>
    public static List<SurveyQuestion> VisibleQuestions(IEnumerable<SurveyQuestion> questions, IReadOnlyDictionary<Guid, SurveyAnswer> answers)
    {
        var visible = new List<SurveyQuestion>();
        foreach (var q in questions.OrderBy(q => q.Order))
        {
            if (q.ShowIf is { } c)
            {
                if (visible.All(v => v.Id != c.QuestionId)) continue;
                if (!answers.TryGetValue(c.QuestionId, out var a) || !Matches(a, c.Value)) continue;
            }
            visible.Add(q);
        }
        return visible;
    }

    private static bool Matches(SurveyAnswer a, string value) => a.Value == value || a.Values.Contains(value);

    /// <summary>Tek bir sorunun cevabındaki sorun; yoksa null.</summary>
    public static string? AnswerError(SurveyQuestion q, SurveyAnswer? a)
    {
        if (!HasValue(a)) return q.Required ? "Bu soru zorunlu." : null;
        var v = a!.Value;
        var inv = CultureInfo.InvariantCulture;
        switch (q.Type)
        {
            case SurveyQuestionType.SingleChoice when !q.Options.Contains(v ?? ""):
            case SurveyQuestionType.MultiChoice when a.Values.Count == 0 || a.Values.Any(x => !q.Options.Contains(x)):
            case SurveyQuestionType.YesNo when v is not (Yes or No):
                return "Geçersiz seçim.";
            case SurveyQuestionType.Rating5 when !int.TryParse(v, NumberStyles.None, inv, out var r) || r is < 1 or > 5:
                return "Puan 1 ile 5 arasında olmalı.";
            case SurveyQuestionType.Nps10 when !int.TryParse(v, NumberStyles.None, inv, out var n) || n is < 0 or > 10:
                return "Puan 0 ile 10 arasında olmalı.";
            case SurveyQuestionType.Number when !double.TryParse(v, NumberStyles.Float, inv, out _):
                return "Sayı girin.";
            case SurveyQuestionType.Date when !DateOnly.TryParseExact(v, "yyyy-MM-dd", inv, DateTimeStyles.None, out _):
                return "Geçerli bir tarih girin.";
            case SurveyQuestionType.ShortText or SurveyQuestionType.LongText when v?.Length > MaxAnswerLength:
                return $"En fazla {MaxAnswerLength} karakter girilebilir.";
            case SurveyQuestionType.Photo when !Guid.TryParse(v, out _):
                return "Fotoğraf eklenemedi.";
            case SurveyQuestionType.Location when !IsLocation(v):
                return "Konum alınamadı.";
        }
        return null;
    }

    private static bool IsLocation(string? v)
    {
        var parts = v?.Split(',') ?? [];
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) && lat is >= -90 and <= 90
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng) && lng is >= -180 and <= 180;
    }

    /// <summary>Kişi bilgisi ayarına göre ad, telefon ve onay sorunları.</summary>
    public static List<string> RespondentErrors(SurveyRespondentMode mode, string? name, string? phone, bool consent)
    {
        var errors = new List<string>();
        if (mode == SurveyRespondentMode.None) return errors;
        var given = !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(phone);
        if (mode == SurveyRespondentMode.Required && string.IsNullOrWhiteSpace(name)) errors.Add("Kişinin adı zorunlu.");
        if ((given || mode == SurveyRespondentMode.Required) && !consent) errors.Add("Kişi bilgisi için onay gerekli.");
        return errors;
    }

    /// <summary>Tüm yanıtı doğrular. Gizli soruların cevapları dikkate alınmaz.</summary>
    public static List<string> ValidateAnswers(IReadOnlyList<SurveyQuestion> questions, SurveyRespondentMode mode,
        IEnumerable<SurveyAnswer> answers, string? name, string? phone, bool consent)
    {
        var errors = RespondentErrors(mode, name, phone, consent);
        var map = ToMap(answers);
        foreach (var q in VisibleQuestions(questions, map))
            if (AnswerError(q, map.GetValueOrDefault(q.Id)) is { } e)
                errors.Add($"'{q.Text}': {e}");
        return errors;
    }

    /// <summary>Kaydedilecek cevaplar: yalnızca görünen ve dolu sorular.</summary>
    public static List<SurveyAnswer> VisibleAnswers(IReadOnlyList<SurveyQuestion> questions, IEnumerable<SurveyAnswer> answers)
    {
        var map = ToMap(answers);
        return VisibleQuestions(questions, map)
            .Select(q => map.GetValueOrDefault(q.Id)).Where(HasValue).Select(a => a!).ToList();
    }

    private static Dictionary<Guid, SurveyAnswer> ToMap(IEnumerable<SurveyAnswer> answers) =>
        answers.GroupBy(a => a.QuestionId).ToDictionary(g => g.Key, g => g.Last());
}
