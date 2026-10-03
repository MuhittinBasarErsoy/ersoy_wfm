using System.Globalization;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Application;

/// <summary>Görev tipinin dinamik alan ve tamamlama kurallarını doğrular. Hem API hem istemci aynı kuralları kullanır.</summary>
public static class TaskRules
{
    /// <summary>Görev oluşturulurken (dispeçer tarafı) doldurulması gereken alanları doğrular.</summary>
    public static List<string> ValidateCreationFields(TaskType type, IDictionary<string, string?> values) =>
        ValidateFields(type.Fields.Where(f => !f.FilledOnCompletion), values);

    /// <summary>Görev tamamlanırken gereksinimleri doğrular.</summary>
    public static List<string> ValidateCompletion(TaskType type, IDictionary<string, string?> values,
        int photoCount, bool hasSignature, string? note, double? distanceMeters)
    {
        var errors = ValidateFields(type.Fields.Where(f => f.FilledOnCompletion), values);
        var c = type.Completion;

        if (c.RequireSignature && !hasSignature) errors.Add("Müşteri imzası zorunlu.");
        var minPhotos = Math.Max(c.RequirePhoto ? 1 : 0, c.MinPhotoCount);
        if (photoCount < minPhotos) errors.Add($"En az {minPhotos} fotoğraf gerekli (mevcut: {photoCount}).");
        if (c.RequireNote && string.IsNullOrWhiteSpace(note)) errors.Add("Tamamlama notu zorunlu.");
        if (c.MaxDistanceMeters > 0)
        {
            if (distanceMeters is null)
                errors.Add("Tamamlamak için konum bilgisi gerekli.");
            else if (distanceMeters > c.MaxDistanceMeters)
                errors.Add($"Görev konumuna çok uzaktasınız ({distanceMeters:0} m > {c.MaxDistanceMeters} m).");
        }
        return errors;
    }

    public static List<string> ValidateFields(IEnumerable<FieldDefinition> fields, IDictionary<string, string?> values)
    {
        var errors = new List<string>();
        foreach (var f in fields)
        {
            values.TryGetValue(f.Key, out var v);
            if (string.IsNullOrWhiteSpace(v))
            {
                if (f.Required && f.Type != FieldType.Checkbox) errors.Add($"'{f.Label}' alanı zorunlu.");
                if (f.Required && f.Type == FieldType.Checkbox) errors.Add($"'{f.Label}' işaretlenmeli.");
                continue;
            }
            switch (f.Type)
            {
                case FieldType.Number when !double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out _):
                    errors.Add($"'{f.Label}' sayısal olmalı.");
                    break;
                case FieldType.Date when !DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                    errors.Add($"'{f.Label}' geçerli bir tarih olmalı.");
                    break;
                case FieldType.Select when f.Options.Count > 0 && !f.Options.Contains(v):
                    errors.Add($"'{f.Label}' için geçersiz seçim.");
                    break;
                case FieldType.Checkbox when f.Required && v != "true":
                    errors.Add($"'{f.Label}' işaretlenmeli.");
                    break;
            }
        }
        return errors;
    }

    /// <summary>Alan tanımlarının kendisini doğrular (görev tipi editörü için).</summary>
    public static List<string> ValidateTaskType(string name, List<FieldDefinition> fields)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add("Görev tipi adı zorunlu.");
        foreach (var f in fields)
        {
            if (string.IsNullOrWhiteSpace(f.Key) || !f.Key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
                errors.Add($"'{f.Label}' alanının anahtarı yalnızca harf, rakam ve _ içermeli.");
            if (string.IsNullOrWhiteSpace(f.Label)) errors.Add($"'{f.Key}' alanının etiketi boş olamaz.");
            if (f.Type == FieldType.Select && f.Options.Count == 0) errors.Add($"'{f.Label}' seçim alanı için seçenek girin.");
        }
        foreach (var dup in fields.GroupBy(f => f.Key).Where(g => g.Count() > 1))
            errors.Add($"'{dup.Key}' anahtarı birden fazla alanda kullanılmış.");
        return errors;
    }
}
