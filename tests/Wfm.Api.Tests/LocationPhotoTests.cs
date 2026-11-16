using System.Net;
using System.Text.Json;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Api.Tests;

public class LocationPhotoTests
{
    [Fact]
    public void Existing_tasks_do_not_require_location_photos()
    {
        var task = JsonSerializer.Deserialize<WorkTaskDetailDto>("{\"title\":\"Eski görev\"}", WfmJson.Options)!;
        Assert.False(task.RequireLocationPhoto);
        Assert.Empty(TaskRules.ValidateCompletion(new TaskType(), new Dictionary<string, string?>(), 0, false, null, null));
    }

    [Fact]
    public void Ordinary_photos_do_not_satisfy_location_photo_requirement()
    {
        var errors = TaskRules.ValidateCompletion(new TaskType(), new Dictionary<string, string?>(),
            3, false, null, null, requireLocationPhoto: true, locationPhotoCount: 0);
        Assert.Contains(errors, e => e.Contains("Konum ve çekim zamanı"));
    }

    [Fact]
    public void Stamped_photo_satisfies_the_requirement_but_keeps_other_rules()
    {
        var type = new TaskType { Completion = new CompletionRequirements { RequirePhoto = true, MinPhotoCount = 2 } };
        var errors = TaskRules.ValidateCompletion(type, new Dictionary<string, string?>(),
            1, false, null, null, requireLocationPhoto: true, locationPhotoCount: 1);
        Assert.Single(errors);
        Assert.Contains("En az 2", errors[0]);
        Assert.Empty(TaskRules.ValidateCompletion(type, new Dictionary<string, string?>(),
            2, false, null, null, requireLocationPhoto: true, locationPhotoCount: 1));
    }

    [Fact]
    public async Task Upload_preserves_capture_metadata_and_legacy_uploads_remain_unstamped()
    {
        var handler = new UploadHandler();
        var options = new WfmClientOptions { ApiBaseUrl = "https://test.invalid" };
        var client = new WfmApiClient(new HttpClient(handler) { BaseAddress = new Uri(options.ApiBaseUrl) }, options);
        var capturedAt = new DateTime(2026, 11, 13, 9, 15, 0, DateTimeKind.Utc);
        await client.UploadAttachmentAsync(Guid.NewGuid(), new MemoryStream([1, 2]), "photo.jpg", "image/jpeg",
            AttachmentKind.Photo, 40.499024, 29.316780, capturedAt, Guid.NewGuid(), true, "Bursa, Türkiye", 5);
        Assert.Equal("true", handler.Fields["locationStamped"]);
        Assert.Equal("40.499024", handler.Fields["latitude"]);
        Assert.Equal(capturedAt.ToString("O"), handler.Fields["capturedAt"]);
        Assert.Equal("Bursa, Türkiye", handler.Fields["captureAddress"]);
        Assert.Equal("5", handler.Fields["accuracy"]);
        await client.UploadAttachmentAsync(Guid.NewGuid(), new MemoryStream([1, 2]), "old.jpg", "image/jpeg",
            AttachmentKind.Photo, 40, 29, capturedAt, Guid.NewGuid());
        Assert.DoesNotContain("locationStamped", handler.Fields.Keys);
    }

    private sealed class UploadHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Fields { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Fields = [];
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in form)
                Fields[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync(cancellationToken);
            var attachment = new AttachmentDto(Guid.NewGuid(), AttachmentKind.Photo, "photo.jpg", "image/jpeg", "/photo",
                40, 29, DateTime.UtcNow, Guid.NewGuid());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(attachment, WfmJson.Options))
            };
        }
    }
}
