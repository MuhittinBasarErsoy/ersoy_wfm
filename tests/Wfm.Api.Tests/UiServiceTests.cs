using System.IO.Compression;
using System.Xml.Linq;
using Wfm.Application.Contracts;
using Wfm.Domain.Enums;
using Wfm.Shared.UI.Services;

namespace Wfm.Api.Tests;

/// <summary>Arayüz katmanındaki saf mantık: varış algılama, Excel/CSV üretimi, sorgu ayrıştırma.</summary>
public class UiServiceTests
{
    [Fact]
    public async Task Arrival_is_suggested_once_within_150m_and_again_after_leaving()
    {
        var task = new WorkTaskDetailDto { Id = Guid.NewGuid(), Title = "Teslimat", Status = WorkTaskStatus.EnRoute, Latitude = 41.0, Longitude = 29.0 };
        var other = new WorkTaskDetailDto { Id = Guid.NewGuid(), Title = "Kabul edilmiş", Status = WorkTaskStatus.Accepted, Latitude = 41.0, Longitude = 29.0 };
        var presenter = new FakePresenter();
        var detector = new ArrivalDetector(new FakeField([task, other]), presenter);
        var arrived = new List<Guid>();
        detector.Arrived += (t, _) => arrived.Add(t.Id);

        await detector.OnLocationAsync(41.01, 29.0);    // ~1.1 km: öneri yok
        await detector.OnLocationAsync(41.0009, 29.0);  // ~100 m: öneri
        await detector.OnLocationAsync(41.0005, 29.0);  // hâlâ yakın: tekrar yok
        Assert.Equal([task.Id], arrived);
        Assert.Single(presenter.Shown);

        await detector.OnLocationAsync(41.006, 29.0);   // ~670 m uzaklaştı
        await detector.OnLocationAsync(41.0003, 29.0);  // yeniden yaklaştı: tekrar önerilir
        Assert.Equal(2, arrived.Count);
        Assert.DoesNotContain(other.Id, arrived);       // yalnızca "Yolda" görevler
    }

    [Fact]
    public void Xlsx_is_a_valid_package_with_typed_cells()
    {
        var bytes = Xlsx.Build("Görevler: [1]", ["Başlık", "Sayı", "Tarih", "Boş"],
            [new object?[] { "A & <b> \u0001", 12.5, new DateTime(2026, 10, 4, 14, 30, 0), null }]);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        var wb = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal("Görevler- -1-", wb.Descendants(ns + "sheet").Single().Attribute("name")!.Value); // geçersiz karakterler temizlenir
        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var cells = sheet.Descendants(ns + "row").ElementAt(1).Elements(ns + "c").ToList();
        Assert.Equal("A & <b> ", cells[0].Value);                            // kaçış yapılmış, kontrol karakteri atılmış
        Assert.Equal("12.5", cells[1].Element(ns + "v")!.Value);             // sayı olarak
        Assert.Equal("2", cells[2].Attribute("s")!.Value);                   // tarih biçimi
        Assert.Equal(46299.604166, double.Parse(cells[2].Element(ns + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture), 5);
        Assert.Equal(3, cells.Count);                                        // boş hücre yazılmaz
    }

    [Fact]
    public void Csv_escapes_separators_and_blocks_formula_injection()
    {
        var csv = Csv.Build(["A", "B"], [new[] { "x;y", "=HYPERLINK(\"kötü\")" }]);
        var line = csv.Split(Environment.NewLine)[1];
        Assert.Equal("\"x;y\";\"'=HYPERLINK(\"\"kötü\"\")\"", line);
    }

    [Fact]
    public void Query_string_parses_repeated_and_encoded_values()
    {
        var q = QueryString.Parse("?status=Draft&status=Rejected&q=%C3%A7i%C3%A7ek+d%C3%BCnyas%C4%B1&open=1");
        Assert.Equal(["Draft", "Rejected"], q["status"]);
        Assert.Equal("çiçek dünyası", q["q"][0]);
        Assert.Equal("1", q["OPEN"][0]); // büyük/küçük harf duyarsız
    }

    private sealed class FakePresenter : INotificationPresenter
    {
        public List<string> Shown { get; } = [];
        public Task ShowAsync(string title, string body, Guid? taskId) { Shown.Add(body); return Task.CompletedTask; }
    }

    private sealed class FakeField(IReadOnlyList<WorkTaskDetailDto> tasks) : IFieldService
    {
        public event Action? Changed { add { } remove { } }
        public bool IsOnline => true;
        public int PendingCount => 0;
        public DateTime? LastSync => null;
        public string? LastError => null;
        public Task<IReadOnlyList<WorkTaskDetailDto>> GetTasksAsync() => Task.FromResult(tasks);
        public Task<WorkTaskDetailDto?> GetTaskAsync(Guid id) => Task.FromResult(tasks.FirstOrDefault(t => t.Id == id));
        public Task<TaskTypeDto?> GetTaskTypeAsync(Guid id) => Task.FromResult<TaskTypeDto?>(null);
        public Task SyncAsync() => Task.CompletedTask;
        public Task ChangeStatusAsync(Guid taskId, ChangeStatusRequest request) => Task.CompletedTask;
        public Task ChangeStageAsync(Guid taskId, ChangeStageRequest request) => Task.CompletedTask;
        public Task AddAttachmentAsync(Guid taskId, CapturedFile file, AttachmentKind kind, GeoPoint? location) => Task.CompletedTask;
        public Task RemoveAttachmentAsync(Guid taskId, Guid attachmentId) => Task.CompletedTask;
        public Task<ShiftDto?> GetShiftAsync() => Task.FromResult<ShiftDto?>(null);
        public Task<ShiftDto?> SetShiftAsync(bool onShift) => Task.FromResult<ShiftDto?>(null);
        public Task OnServerTaskChangedAsync(WorkTaskDto task) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}
