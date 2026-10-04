using System.IO.Compression;
using System.Security;
using System.Text;

namespace Wfm.Shared.UI.Services;

/// <summary>
/// Bağımlılıksız, tek sayfalık .xlsx üretici (OpenXML SpreadsheetML). Başlık satırı kalın ve dondurulmuş,
/// sayısal hücreler sayı olarak yazılır; Excel'de doğrudan filtrelenip toplanabilir.
/// </summary>
public static class Xlsx
{
    public static byte[] Build(string sheetName, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="{E(Truncate(sheetName, 31))}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            // Stil 0: normal, 1: kalın başlık (gri dolgu), 2: tarih-saat.
            Add(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <numFmts count="1"><numFmt numFmtId="164" formatCode="dd.mm.yyyy hh:mm"/></numFmts>
                <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>
                <fill><patternFill patternType="solid"><fgColor rgb="FFE8EEF6"/></patternFill></fill></fills>
                <borders count="1"><border/></borders>
                <cellStyleXfs count="1"><xf/></cellStyleXfs>
                <cellXfs count="3"><xf/><xf fontId="1" fillId="2" applyFont="1" applyFill="1"/><xf numFmtId="164" applyNumberFormat="1"/></cellXfs>
                </styleSheet>
                """);

            var sb = new StringBuilder();
            sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
            sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
            // Sütun genişliği içeriğe göre (tarih ~16 karakter); çok uzun metinler 60'ta kesilir.
            var data = rows.ToList();
            sb.Append("<cols>");
            for (var c = 0; c < header.Count; c++)
            {
                var col = c;
                var longest = data.Select(r => col < r.Count ? CellLength(r[col]) : 0).DefaultIfEmpty(0).Max();
                sb.Append($"""<col min="{c + 1}" max="{c + 1}" width="{Math.Clamp(Math.Max(header[c].Length + 4, longest + 2), 10, 60)}" customWidth="1"/>""");
            }
            sb.Append("</cols><sheetData>");
            AppendRow(sb, 1, header.Cast<object?>().ToList(), header: true);
            var r = 2;
            foreach (var row in data) AppendRow(sb, r++, row, header: false);
            sb.Append("</sheetData>");
            if (r > 2) sb.Append($"""<autoFilter ref="A1:{Col(header.Count - 1)}{r - 1}"/>""");
            sb.Append("</worksheet>");
            Add(zip, "xl/worksheets/sheet1.xml", sb.ToString());
        }
        return ms.ToArray();
    }

    private static void AppendRow(StringBuilder sb, int r, IReadOnlyList<object?> cells, bool header)
    {
        sb.Append($"<row r=\"{r}\">");
        for (var c = 0; c < cells.Count; c++)
        {
            var refName = $"{Col(c)}{r}";
            switch (cells[c])
            {
                case null:
                    break;
                case int or long or double or decimal when !header:
                    sb.Append($"<c r=\"{refName}\"><v>{Convert.ToString(cells[c], System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case DateTime dt when !header:
                    // Excel seri tarih: 1899-12-30'dan bu yana gün.
                    var serial = (dt - new DateTime(1899, 12, 30)).TotalDays;
                    sb.Append($"<c r=\"{refName}\" s=\"2\"><v>{serial.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                    break;
                default:
                    sb.Append($"<c r=\"{refName}\" t=\"inlineStr\"{(header ? " s=\"1\"" : "")}><is><t xml:space=\"preserve\">{E(cells[c]!.ToString())}</t></is></c>");
                    break;
            }
        }
        sb.Append("</row>");
    }

    private static int CellLength(object? v) => v switch
    {
        null => 0,
        DateTime => 17,
        double d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Length,
        _ => v.ToString()?.Length ?? 0
    };

    private static string Col(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    // XML'de geçersiz kontrol karakterlerini at, kalanı kaçışla.
    private static string E(string? v) =>
        SecurityElement.Escape(new string((v ?? "").Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ').ToArray())) ?? "";

    private static string Truncate(string s, int max)
    {
        s = new string(s.Select(ch => "[]:*?/\\".Contains(ch) ? '-' : ch).ToArray());
        return s.Length <= max ? s : s[..max];
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content.Trim());
    }
}
