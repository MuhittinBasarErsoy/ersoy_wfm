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
