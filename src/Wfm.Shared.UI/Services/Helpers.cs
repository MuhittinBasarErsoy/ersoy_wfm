using System.Text;
using MudBlazor;
using Wfm.Shared.UI.Components;

namespace Wfm.Shared.UI.Services;

/// <summary>Sorgu dizesi ayrıştırma (MAUI'de ASP.NET WebUtilities olmadığı için).</summary>
public static class QueryString
{
    public static Dictionary<string, List<string>> Parse(string? query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(value);
        }
        return result;
    }
}

/// <summary>Excel uyumlu CSV (noktalı virgül ayraçlı; Türkçe Excel varsayılanı).</summary>
public static class Csv
{
    public static string Build(IEnumerable<string> header, IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', header.Select(Escape)));
        foreach (var r in rows) sb.AppendLine(string.Join(';', r.Select(Escape)));
        return sb.ToString();
    }

    private static string Escape(string? v)
    {
        v ??= "";
        // Formül enjeksiyonunu önle (=, +, -, @ ile başlayan hücreler).
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@') v = "'" + v;
        return v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}

public static class Prompt
{
    /// <summary>Tek satırlık metin sorar; vazgeçilirse null.</summary>
    public static async Task<string?> AskAsync(IDialogService dialogs, string title, string label, string? placeholder = null, string? initial = null)
    {
        var parameters = new DialogParameters<PromptDialog>
        {
            { x => x.Label, label }, { x => x.Placeholder, placeholder }, { x => x.Value, initial }
        };
        var dialog = await dialogs.ShowAsync<PromptDialog>(title, parameters, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, CloseOnEscapeKey = true });
        var result = await dialog.Result;
        return result is { Canceled: false, Data: string s } ? s : null;
    }
}
