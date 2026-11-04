using System.Net;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Api.Tests;

/// <summary>Anket motoru: tanım, sürümler ve cevap kuralları.</summary>
public class SurveyTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Dispatcher = "dispecer@tamir.local";
    private const string Worker = "teknisyen1@tamir.local";
    private const string OtherTenant = "dispecer@cicek.local";

    private static SurveyQuestion Choice(string text, int order, params string[] options) =>
        new() { Order = order, Type = SurveyQuestionType.SingleChoice, Text = text, Options = [.. options], Required = true };

    private static SurveyQuestion YesNo(string text, int order) =>
        new() { Order = order, Type = SurveyQuestionType.YesNo, Text = text, Required = true };

    private static SurveyQuestion Text(string text, int order, SurveyCondition? showIf = null, bool required = true) =>
        new() { Order = order, Type = SurveyQuestionType.ShortText, Text = text, Required = required, ShowIf = showIf };

    private static SaveSurveyRequest Request(string title, params SurveyQuestion[] questions) => new() { Title = title, Questions = [.. questions] };

    private static SurveyAnswer A(SurveyQuestion q, string value) => new() { QuestionId = q.Id, Value = value };

    [Fact]
    public async Task Publishing_creates_a_version_and_later_edits_stay_as_draft()
    {
        var dispatcher = await api.LoginAs(Dispatcher);
        var q = Choice("Memnun musunuz?", 1, "Evet", "Hayır");
        var draft = await dispatcher.CreateSurveyAsync(Request("Sürüm testi", q));
        Assert.Equal(SurveyStatus.Draft, draft.Status);
        Assert.Null(draft.CurrentVersionId);

        var v1 = await dispatcher.PublishSurveyAsync(draft.Id);
        Assert.Equal(SurveyStatus.Published, v1.Status);
        Assert.Equal(1, v1.CurrentVersionNo);
        Assert.False(v1.HasUnpublishedChanges);

        // Düzenleme yayımı etkilemez, soru kimliği korunur.
        var edit = Request("Sürüm testi", v1.Questions[0], Text("Neden?", 2, required: false));
        var edited = await dispatcher.UpdateSurveyAsync(draft.Id, edit);
        Assert.True(edited.HasUnpublishedChanges);
        Assert.Equal(v1.CurrentVersionId, edited.CurrentVersionId);
        Assert.Equal(q.Id, edited.Questions[0].Id);

        var v2 = await dispatcher.PublishSurveyAsync(draft.Id);
        Assert.Equal(2, v2.CurrentVersionNo);
        Assert.NotEqual(v1.CurrentVersionId, v2.CurrentVersionId);

        // Değişiklik yokken yeniden yayımlamak yeni sürüm açmaz; arşivden geri almak için kullanılır.
        var archived = await dispatcher.ArchiveSurveyAsync(draft.Id);
        Assert.Equal(SurveyStatus.Archived, archived.Status);
        var restored = await dispatcher.PublishSurveyAsync(draft.Id);
        Assert.Equal(SurveyStatus.Published, restored.Status);
        Assert.Equal(2, restored.CurrentVersionNo);

        var delete = await Assert.ThrowsAsync<ApiException>(() => dispatcher.DeleteSurveyAsync(draft.Id));
        Assert.Equal(HttpStatusCode.BadRequest, delete.Status);
    }

    [Fact]
    public async Task Incomplete_draft_saves_but_cannot_be_published()
    {
        var dispatcher = await api.LoginAs(Dispatcher);
        var draft = await dispatcher.CreateSurveyAsync(Request("Yarım taslak", Choice("Tek seçenek", 1, "A")));
        var bad = await Assert.ThrowsAsync<ApiException>(() => dispatcher.PublishSurveyAsync(draft.Id));
        Assert.Contains("en az iki seçenek", bad.Message);

        await dispatcher.DeleteSurveyAsync(draft.Id);
        Assert.DoesNotContain(await dispatcher.GetSurveysAsync(), s => s.Id == draft.Id);
    }

    [Fact]
    public async Task Surveys_are_tenant_scoped_and_only_managers_write()
    {
        var dispatcher = await api.LoginAs(Dispatcher);
        var survey = await dispatcher.CreateSurveyAsync(Request("Kiracı testi", YesNo("Soru", 1)));

        var other = await api.LoginAs(OtherTenant);
        Assert.DoesNotContain(await other.GetSurveysAsync(), s => s.Id == survey.Id);
        var notFound = await Assert.ThrowsAsync<ApiException>(() => other.GetSurveyAsync(survey.Id));
        Assert.Equal(HttpStatusCode.NotFound, notFound.Status);

        var worker = await api.LoginAs(Worker);
        var forbidden = await Assert.ThrowsAsync<ApiException>(() => worker.CreateSurveyAsync(Request("Yetkisiz", YesNo("Soru", 1))));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
    }

    // ---------- Sahada anket görevi ----------

    private async Task<(WfmApiClient dispatcher, WfmApiClient worker, Guid workerId, Guid paper)> FieldSetup()
    {
        var dispatcher = await api.LoginAs(Dispatcher);
        var worker = await api.LoginAs(Worker);
        var paper = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Evrak İşleri").Id; // masa başı: kabulden sonra doldurulur
        return (dispatcher, worker, (await worker.MeAsync()).Id, paper);
    }

    private static async Task<SurveyDto> Published(WfmApiClient dispatcher, string title, SurveyRespondentMode mode, params SurveyQuestion[] questions)
    {
        var req = Request(title, questions);
        req.RespondentMode = mode;
        if (mode != SurveyRespondentMode.None) req.ConsentText = Survey.DefaultConsentText;
        return await dispatcher.PublishSurveyAsync((await dispatcher.CreateSurveyAsync(req)).Id);
    }

    private static Task<WorkTaskDto> NewTask(WfmApiClient dispatcher, Guid type, Guid? assignee, Guid? survey, int target = 1) =>
        dispatcher.CreateTaskAsync(new SaveWorkTaskRequest
        {
            TaskTypeId = type, Title = "Anket görevi", AssigneeId = assignee, SurveyId = survey, SurveyTargetCount = target
        });

    private static SubmitSurveyResponseRequest Response(params SurveyAnswer[] answers) => new()
    {
        ClientId = Guid.NewGuid(), Answers = [.. answers], StartedAt = DateTime.UtcNow.AddMinutes(-2), SubmittedAt = DateTime.UtcNow,
        Latitude = 41.03, Longitude = 28.98
    };

    [Fact]
    public async Task Worker_fills_until_target_then_completes()
    {
        var (dispatcher, worker, workerId, paper) = await FieldSetup();
        var q = YesNo("Memnun musunuz?", 1);
        var survey = await Published(dispatcher, "Hedefli", SurveyRespondentMode.None, q);
        var task = await NewTask(dispatcher, paper, workerId, survey.Id, target: 2);
        var question = (await worker.GetTaskAsync(task.Id)).Survey!.Questions.Single();
        Assert.Equal("Hedefli", (await worker.GetTaskAsync(task.Id)).Survey!.Title);

        // Görev kabul edilmeden doldurulamaz.
        var early = await Assert.ThrowsAsync<ApiException>(() => worker.SubmitSurveyResponseAsync(task.Id, Response(A(question, SurveyRules.Yes))));
        Assert.Contains("önce görev", early.Message);
        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });

        var bad = await Assert.ThrowsAsync<ApiException>(() => worker.SubmitSurveyResponseAsync(task.Id, Response()));
        Assert.Contains("zorunlu", bad.Message);

        var first = Response(A(question, SurveyRules.Yes));
        Assert.Equal(1, (await worker.SubmitSurveyResponseAsync(task.Id, first)).ResponseCount);
        Assert.Equal(1, (await worker.SubmitSurveyResponseAsync(task.Id, first)).ResponseCount); // tekrar gönderim

        var incomplete = await Assert.ThrowsAsync<ApiException>(() => worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Completed }));
        Assert.Contains("1 / 2", incomplete.Message);

        Assert.Equal(2, (await worker.SubmitSurveyResponseAsync(task.Id, Response(A(question, SurveyRules.No)))).ResponseCount);
        var done = await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Completed });
        Assert.Equal(WorkTaskStatus.Completed, done.Status);
        Assert.Equal(2, done.SurveyResponseCount);

        // Kapanıştan önce doldurulup sonra gelen internetsiz yanıt kabul edilir, kapanıştan sonrası edilmez.
        var offline = Response(A(question, SurveyRules.Yes));
        offline.SubmittedAt = DateTime.UtcNow.AddMinutes(-1);
        Assert.Equal(3, (await worker.SubmitSurveyResponseAsync(task.Id, offline)).ResponseCount);
        var late = Response(A(question, SurveyRules.Yes));
        late.SubmittedAt = default; // sunucu saati kullanılır: kapanıştan sonra
        var rejected = await Assert.ThrowsAsync<ApiException>(() => worker.SubmitSurveyResponseAsync(task.Id, late));
        Assert.Contains("kapandı", rejected.Message);
    }

    [Fact]
    public async Task Task_stays_on_its_version_and_survey_locks_after_first_response()
    {
        var (dispatcher, worker, workerId, paper) = await FieldSetup();
        var survey = await Published(dispatcher, "Sürümlü", SurveyRespondentMode.None, YesNo("Eski soru", 1));
        var task = await NewTask(dispatcher, paper, workerId, survey.Id);

        var edit = Request("Sürümlü", new SurveyQuestion { Type = SurveyQuestionType.YesNo, Text = "Yeni soru", Order = 1, Required = true });
        await dispatcher.UpdateSurveyAsync(survey.Id, edit);
        await dispatcher.PublishSurveyAsync(survey.Id);

        var detail = await worker.GetTaskAsync(task.Id);
        Assert.Equal("Eski soru", detail.Survey!.Questions.Single().Text);
        Assert.Equal(1, detail.Survey.VersionNo);

        // Yeni görev yeni sürümü alır.
        var newer = await NewTask(dispatcher, paper, workerId, survey.Id);
        Assert.Equal("Yeni soru", (await worker.GetTaskAsync(newer.Id)).Survey!.Questions.Single().Text);

        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        await worker.SubmitSurveyResponseAsync(task.Id, Response(A(detail.Survey.Questions[0], SurveyRules.Yes)));

        var other = await Published(dispatcher, "Başka", SurveyRespondentMode.None, YesNo("Soru", 1));
        var change = new SaveWorkTaskRequest { TaskTypeId = paper, Title = "Anket görevi", AssigneeId = workerId, SurveyId = other.Id };
        var locked = await Assert.ThrowsAsync<ApiException>(() => dispatcher.UpdateTaskAsync(task.Id, change));
        Assert.Contains("değiştirilemez", locked.Message);

        // Yanıtı olan görev silinemez, yanıtlar kaybolmasın.
        await dispatcher.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Cancelled });
        var delete = await Assert.ThrowsAsync<ApiException>(() => dispatcher.DeleteTaskAsync(task.Id));
        Assert.Contains("silinemez", delete.Message);
    }

    [Fact]
    public async Task Only_published_surveys_attach_and_only_assignee_responds()
    {
        var (dispatcher, worker, workerId, paper) = await FieldSetup();
        var draft = await dispatcher.CreateSurveyAsync(Request("Taslak anket", YesNo("Soru", 1)));
        var error = await Assert.ThrowsAsync<ApiException>(() => NewTask(dispatcher, paper, workerId, draft.Id));
        Assert.Contains("yayımlanmış", error.Message);

        var survey = await Published(dispatcher, "Yetki", SurveyRespondentMode.None, YesNo("Soru", 1));
        var zero = await Assert.ThrowsAsync<ApiException>(() => NewTask(dispatcher, paper, workerId, survey.Id, target: 0));
        Assert.Contains("Hedef", zero.Message);

        var task = await NewTask(dispatcher, paper, workerId, survey.Id);
        var other = await api.LoginAs("teknisyen2@tamir.local");
        var forbidden = await Assert.ThrowsAsync<ApiException>(() => other.SubmitSurveyResponseAsync(task.Id, Response()));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);

        var plain = await NewTask(dispatcher, paper, workerId, null);
        var none = await Assert.ThrowsAsync<ApiException>(() => worker.SubmitSurveyResponseAsync(plain.Id, Response()));
        Assert.Contains("anket yok", none.Message);
    }

    [Fact]
    public async Task Respondent_data_needs_consent_and_is_dropped_when_anonymous()
    {
        var (dispatcher, worker, workerId, paper) = await FieldSetup();
        var survey = await Published(dispatcher, "Kişili", SurveyRespondentMode.Required, YesNo("Soru", 1));
        var task = await NewTask(dispatcher, paper, workerId, survey.Id);
        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        var q = (await worker.GetTaskAsync(task.Id)).Survey!.Questions[0];

        var noConsent = Response(A(q, SurveyRules.Yes));
        noConsent.RespondentName = "Ali";
        var bad = await Assert.ThrowsAsync<ApiException>(() => worker.SubmitSurveyResponseAsync(task.Id, noConsent));
        Assert.Contains("onay", bad.Message);

        noConsent.ConsentGiven = true;
        Assert.Equal(1, (await worker.SubmitSurveyResponseAsync(task.Id, noConsent)).ResponseCount);
    }

    [Fact]
    public async Task Survey_photos_stay_out_of_task_attachments_for_old_clients()
    {
        var (dispatcher, worker, workerId, paper) = await FieldSetup();
        var survey = await Published(dispatcher, "Fotoğraflı", SurveyRespondentMode.None,
            new SurveyQuestion { Type = SurveyQuestionType.Photo, Text = "Raf", Order = 1, Required = true });
        var task = await NewTask(dispatcher, paper, workerId, survey.Id);
        await worker.ChangeStatusAsync(task.Id, new() { Status = WorkTaskStatus.Accepted });
        var q = (await worker.GetTaskAsync(task.Id)).Survey!.Questions[0];

        var photoId = Guid.NewGuid();
        using var ms = new MemoryStream([0xFF, 0xD8, 0xFF, 0xD9]);
        await worker.UploadAttachmentAsync(task.Id, ms, "raf.jpg", "image/jpeg", AttachmentKind.SurveyPhoto, null, null, DateTime.UtcNow, photoId);
        await worker.SubmitSurveyResponseAsync(task.Id, Response(A(q, photoId.ToString())));

        // Eski sürümler bu ek türünü okuyamaz; görev listesine hiç girmemeli ve kanıt sayılmamalı.
        Assert.DoesNotContain((await worker.SyncAsync()).Tasks.Single(t => t.Id == task.Id).Attachments, a => a.Id == photoId);
        Assert.Empty((await dispatcher.GetTaskAsync(task.Id)).Attachments);
    }

    [Fact]
    public void Definition_rules_catch_broken_conditions()
    {
        var parent = Choice("Üst", 1, "A", "B");
        Assert.Empty(SurveyRules.ValidateDefinition([parent, Text("Alt", 2, new() { QuestionId = parent.Id, Value = "A" })]));

        // Sonraki soruya bağlanamaz (döngü de böyle önlenir).
        var early = Text("Erken", 1, new() { QuestionId = parent.Id, Value = "A" });
        var later = Choice("Sonra", 2, "A", "B");
        later.Id = parent.Id;
        Assert.Contains(SurveyRules.ValidateDefinition([early, later]), e => e.Contains("önceki"));

        // Silinmiş seçeneğe bağlı koşul ve metin sorusuna bağlı koşul.
        Assert.Contains(SurveyRules.ValidateDefinition([parent, Text("Alt", 2, new() { QuestionId = parent.Id, Value = "Yok" })]), e => e.Contains("artık yok"));
        var text = Text("Metin", 1);
        Assert.Contains(SurveyRules.ValidateDefinition([text, Text("Alt", 2, new() { QuestionId = text.Id, Value = "x" })]), e => e.Contains("bağlanabilir"));

        Assert.Contains(SurveyRules.ValidateDefinition([Choice("Çift", 1, "A", "a")]), e => e.Contains("iki kez"));
        Assert.Contains(SurveyRules.ValidateDefinition([YesNo("Soru", 1)], SurveyRespondentMode.Optional, " "), e => e.Contains("onay"));
        Assert.Contains(SurveyRules.ValidateDefinition([]), e => e.Contains("En az bir soru"));
    }

    [Fact]
    public void Condition_chain_hides_children_of_hidden_questions()
    {
        var q1 = YesNo("Memnun musunuz?", 1);
        var q2 = Choice("Neden değil?", 2, "Fiyat", "Hizmet");
        q2.ShowIf = new() { QuestionId = q1.Id, Value = SurveyRules.No };
        var q3 = Text("Fiyat hakkında yazın", 3, new() { QuestionId = q2.Id, Value = "Fiyat" });
        var q4 = Text("Son söz", 4, required: false);
        SurveyQuestion[] all = [q1, q2, q3, q4];

        Dictionary<Guid, SurveyAnswer> Map(params SurveyAnswer[] a) => a.ToDictionary(x => x.QuestionId);

        Assert.Equal([q1, q4], SurveyRules.VisibleQuestions(all, Map(A(q1, SurveyRules.Yes))));
        Assert.Equal([q1, q2, q4], SurveyRules.VisibleQuestions(all, Map(A(q1, SurveyRules.No))));
        Assert.Equal([q1, q2, q3, q4], SurveyRules.VisibleQuestions(all, Map(A(q1, SurveyRules.No), A(q2, "Fiyat"))));
        // Üst soru "Evet"e dönünce eski alt cevaplar sayılmaz: gizli sorular zorunlu değildir ve kaydedilmez.
        SurveyAnswer[] stale = [A(q1, SurveyRules.Yes), A(q2, "Fiyat"), A(q3, "pahalı")];
        Assert.Equal([q1, q4], SurveyRules.VisibleQuestions(all, stale.ToDictionary(x => x.QuestionId)));
        Assert.Empty(SurveyRules.ValidateAnswers(all, SurveyRespondentMode.None, stale, null, null, false));
        Assert.Equal([q1.Id], SurveyRules.VisibleAnswers(all, stale).Select(a => a.QuestionId));

        // Çoklu seçime bağlı koşul: seçilenler arasında olması yeterli.
        var multi = new SurveyQuestion { Order = 1, Type = SurveyQuestionType.MultiChoice, Text = "Hangileri?", Options = ["A", "B"] };
        var follow = Text("B hakkında", 2, new() { QuestionId = multi.Id, Value = "B" });
        var answer = new SurveyAnswer { QuestionId = multi.Id, Values = ["A", "B"] };
        Assert.Contains(follow, SurveyRules.VisibleQuestions([multi, follow], Map(answer)));
    }

    [Theory]
    [InlineData(SurveyQuestionType.YesNo, "Evet", true)]
    [InlineData(SurveyQuestionType.YesNo, "Belki", false)]
    [InlineData(SurveyQuestionType.Rating5, "5", true)]
    [InlineData(SurveyQuestionType.Rating5, "0", false)]
    [InlineData(SurveyQuestionType.Nps10, "0", true)]
    [InlineData(SurveyQuestionType.Nps10, "11", false)]
    [InlineData(SurveyQuestionType.Number, "12.5", true)]
    [InlineData(SurveyQuestionType.Number, "abc", false)]
    [InlineData(SurveyQuestionType.Date, "2026-11-04", true)]
    [InlineData(SurveyQuestionType.Date, "04.11.2026", false)]
    [InlineData(SurveyQuestionType.Location, "41.03,28.98", true)]
    [InlineData(SurveyQuestionType.Location, "141.03,28.98", false)]
    [InlineData(SurveyQuestionType.Photo, "not-a-guid", false)]
    public void Answer_types_are_validated(SurveyQuestionType type, string value, bool valid)
    {
        var q = new SurveyQuestion { Type = type, Text = "Soru", Required = true };
        Assert.Equal(valid, SurveyRules.AnswerError(q, A(q, value)) is null);
    }

    [Fact]
    public void Required_and_choice_answers_are_validated()
    {
        var q = Choice("Soru", 1, "A", "B");
        Assert.NotNull(SurveyRules.AnswerError(q, null));
        Assert.NotNull(SurveyRules.AnswerError(q, A(q, "C")));
        Assert.Null(SurveyRules.AnswerError(q, A(q, "A")));

        var multi = new SurveyQuestion { Type = SurveyQuestionType.MultiChoice, Text = "Çoklu", Options = ["A", "B"] };
        Assert.Null(SurveyRules.AnswerError(multi, null)); // zorunlu değil
        Assert.NotNull(SurveyRules.AnswerError(multi, new SurveyAnswer { QuestionId = multi.Id, Values = ["A", "Z"] }));
    }

    [Fact]
    public void Respondent_rules_require_consent_when_personal_data_is_given()
    {
        Assert.Empty(SurveyRules.RespondentErrors(SurveyRespondentMode.None, "Ali", null, false));
        Assert.Empty(SurveyRules.RespondentErrors(SurveyRespondentMode.Optional, null, null, false));
        Assert.Single(SurveyRules.RespondentErrors(SurveyRespondentMode.Optional, null, "555", false));
        Assert.Empty(SurveyRules.RespondentErrors(SurveyRespondentMode.Optional, "Ali", null, true));
        Assert.Equal(2, SurveyRules.RespondentErrors(SurveyRespondentMode.Required, null, null, false).Count);
        Assert.Empty(SurveyRules.RespondentErrors(SurveyRespondentMode.Required, "Ali", null, true));
    }
}
