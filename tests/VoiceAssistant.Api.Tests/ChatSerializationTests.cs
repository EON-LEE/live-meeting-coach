using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class ChatSerializationTests
{
    [Fact]
    public async Task TwoGroundedSuggestionsUseOneActualAzureRequestAndStreamOnlyPrimary()
    {
        var chunks = new[] { "We can use ", "JMAP for push updates.", "\n", "JMAP lets us ", "receive updates as they happen." };
        var sse = string.Concat(chunks.Select(text => "data: " + JsonSerializer.Serialize(new
        {
            id = "test", @object = "chat.completion.chunk", created = 1, model = "test",
            choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } }
        }) + "\n\n")) + "data: [DONE]\n\n";
        using var handler = new CaptureHandler { ResponseBody = sse };
        using var http = new HttpClient(handler);
        var client = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only-key"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        var provider = new AzureMeetingProvider(new ServiceSettings { ChatDeployment = "test" }, client, null);
        var updates = new List<ReplyUpdate>();
        await foreach (var update in provider.AnswerWithSuggestionsAsync(
            [new("Our rollout uses JMAP."), new("How does it help?")],
            new("grounded", [new("JMAP supports push updates.", new("Notes", "https://example.test/notes", null))]),
            SessionOptions.Legacy, "knowledge", CancellationToken.None))
            updates.Add(update);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("We can use JMAP for push updates.", string.Concat(updates.Select(update => update.Text)));
        Assert.Equal("JMAP lets us receive updates as they happen.", updates.Last().Alternative);
        Assert.All(updates, update => Assert.DoesNotContain('\n', update.Text));
        Assert.Contains("JMAP supports push updates.", handler.RequestBody);
        Assert.Contains("TWO alternative English replies", handler.RequestBody);
        Assert.Contains("SAME supplied evidence", handler.RequestBody);
        Assert.Contains("DIFFERENT conversational", handler.RequestBody);
        Assert.Contains("NOT simply paraphrase", handler.RequestBody);
        Assert.Contains("ONE unit of meaning", handler.RequestBody);
        Assert.Contains("Missing documents alone", handler.RequestBody);
        Assert.Contains("follow-up question", handler.RequestBody);
        Assert.Contains("Korean-speaking IT engineer", handler.RequestBody);
        Assert.Contains("CEFR A2-B1", handler.RequestBody);
        Assert.Contains("at most 18 words", handler.RequestBody);
        Assert.Contains("preserve every compared number", handler.RequestBody);
        Assert.Contains("Never convert one or estimate an equivalent", handler.RequestBody);
        Assert.Contains("Answer every material clause", handler.RequestBody);
        Assert.Contains("use up to 24 words across both", handler.RequestBody);
        Assert.Contains("complete the evidence or uncertainty", handler.RequestBody);
        Assert.Contains("several material clauses", handler.RequestBody);
        Assert.DoesNotContain("three or more specific numbers", handler.RequestBody);
        Assert.Contains("missing detail is not in the material or needs verification", handler.RequestBody);
        Assert.Contains("Do not silently skip that clause", handler.RequestBody);
        Assert.Contains("unknown commitment, not ambiguity", handler.RequestBody);
        Assert.Contains("CURRENT RESPONSE TARGET", handler.RequestBody);
        Assert.Contains("OLDER CONTEXT", handler.RequestBody);
        Assert.Contains("Never substitute an answer to older context", handler.RequestBody);
        Assert.Contains("cover every named option in the primary reply", handler.RequestBody);
        Assert.Contains("silently check the primary reply against every material clause and named option", handler.RequestBody);
        Assert.Contains("The alternative does not count toward the primary reply", handler.RequestBody);
        Assert.Contains("Untrusted response focus", handler.RequestBody);
        Assert.Contains("independently safe and useful", handler.RequestBody);
        Assert.Contains("never use the alternative to defer required information", handler.RequestBody);
        Assert.Contains("How does it help?", handler.RequestBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    public async Task ActualAzureClientSerializesModernCompletionBudgetAndOnlyAConfiguredReasoningEffort(string? effort)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only-key"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        var options = AzureMeetingProvider.CreateChatOptions(2048, effort);
        await foreach (var _ in client.GetChatClient("test-deployment").CompleteChatStreamingAsync(
            [new UserChatMessage("Original synthetic test prompt.")], options)) { }
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(2048, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(json.RootElement.TryGetProperty("temperature", out _));
        // Unset sends nothing, so the deployment default applies exactly as before; a configured effort is sent as-is.
        if (effort is null) Assert.False(json.RootElement.TryGetProperty("reasoning_effort", out _));
        else Assert.Equal(effort, json.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task TheLiveAnswerRequestIsUnchangedWhileStreamUsageIsOff()
    {
        using var handler = new CaptureHandler();
        await DrainAnswerAsync(handler, new ServiceSettings { ChatDeployment = "test" });
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(["max_completion_tokens", "messages", "model", "stream"],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task StreamUsageAsksForTheAnswersUsageAndCountsItsTokensByType()
    {
        var tokens = new List<(long Value, string Operation, string Type)>();
        using var listener = ListenToTokens(tokens);
        var usage = new
        {
            id = "test", @object = "chat.completion.chunk", created = 1, model = "test", choices = Array.Empty<object>(),
            usage = new
            {
                prompt_tokens = 3001, completion_tokens = 41, total_tokens = 3042,
                prompt_tokens_details = new { cached_tokens = 2048 }, completion_tokens_details = new { reasoning_tokens = 7 }
            }
        };
        using var handler = new CaptureHandler
        {
            ResponseBody = "data: " + JsonSerializer.Serialize(new
            {
                id = "test", @object = "chat.completion.chunk", created = 1, model = "test",
                choices = new[] { new { index = 0, delta = new { content = "Sure." }, finish_reason = (string?)"stop" } }
            }) + "\n\ndata: " + JsonSerializer.Serialize(usage) + "\n\ndata: [DONE]\n\n"
        };
        await DrainAnswerAsync(handler, new ServiceSettings { ChatDeployment = "test", ChatStreamUsage = true });
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(json.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Single(json.RootElement.GetProperty("stream_options").EnumerateObject());
        lock (tokens)
            Assert.Equal([(3001L, "answer", "input"), (2048L, "answer", "cached"), (41L, "answer", "output"), (7L, "answer", "reasoning")],
                tokens.Where(token => token.Value is 3001 or 2048 or 41 or 7).ToArray());
    }

    [Fact]
    public async Task TheClassifierAndTranslationCountTheirTokensWithoutStreamOptions()
    {
        var tokens = new List<(long Value, string Operation, string Type)>();
        using var listener = ListenToTokens(tokens);
        string Completion(string text) => JsonSerializer.Serialize(new
        {
            id = "test", @object = "chat.completion", created = 1, model = "test",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
            usage = new
            {
                prompt_tokens = 9101, completion_tokens = 9103, total_tokens = 18204,
                prompt_tokens_details = new { cached_tokens = 9102 }, completion_tokens_details = new { reasoning_tokens = 9104 }
            }
        });
        using var classifier = new CaptureHandler { ResponseBody = Completion("RESPOND"), ContentType = "application/json" };
        Assert.True(await Provider(classifier, new ServiceSettings { ChatDeployment = "test", ChatStreamUsage = true })
            .ShouldRespondAsync([new("Any questions?")], CancellationToken.None));
        Assert.DoesNotContain("stream_options", classifier.RequestBody);
        using var translation = new CaptureHandler { ResponseBody = Completion("좋습니다."), ContentType = "application/json" };
        Assert.Equal("좋습니다.", await Provider(translation, new ServiceSettings { ChatDeployment = "test", ChatStreamUsage = true })
            .TranslateReplyAsync("Sounds good.", TranslationContext.Empty, CancellationToken.None));
        Assert.DoesNotContain("stream_options", translation.RequestBody);
        string[] types = ["input", "cached", "output", "reasoning"];
        lock (tokens)
            Assert.Equal(types.Select((type, index) => (9101L + index, "classifier", type))
                .Concat(types.Select((type, index) => (9101L + index, "translation", type))).ToArray(),
                tokens.Where(token => token.Value is >= 9101 and <= 9104).ToArray());
    }

    [Fact]
    public async Task NoAnswerFlagSendsAnythingEvenForASessionWithACacheScope()
    {
        using var handler = new CaptureHandler();
        await DrainAnswerAsync(handler, new ServiceSettings { ChatDeployment = "test", ChatPromptCacheKey = "off" }, Scoped);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(["max_completion_tokens", "messages", "model", "stream"],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("priority")]
    [InlineData("flex")]
    public async Task AServiceTierIsSentWithTheAnswerOnly(string tier)
    {
        var settings = new ServiceSettings { ChatDeployment = "test", ChatServiceTier = tier };
        using var answer = new CaptureHandler();
        await DrainAnswerAsync(answer, settings, Scoped);
        using (var json = JsonDocument.Parse(answer.RequestBody!))
            Assert.Equal(tier, json.RootElement.GetProperty("service_tier").GetString());
        var (classifier, translation) = await AuxiliaryBodiesAsync(settings);
        Assert.DoesNotContain("service_tier", classifier);
        Assert.DoesNotContain("service_tier", translation);
    }

    [Fact]
    public async Task AStaticPromptCacheKeyIsOneConstantForTheDeploymentAndPromptVersion()
    {
        var settings = new ServiceSettings { ChatDeployment = "test", ChatPromptCacheKey = "static" };
        using var first = new CaptureHandler();
        using var second = new CaptureHandler();
        await DrainAnswerAsync(first, settings, Scoped);
        await DrainAnswerAsync(second, settings, Scoped with { CacheScope = "another-session" });
        var key = CacheKey(first.RequestBody!);
        Assert.Equal(key, CacheKey(second.RequestBody!));
        Assert.Matches("^answer-[0-9a-f]{16}$", key);
        Assert.Equal(AzureMeetingProvider.StaticCacheKey("test"), key);
        Assert.NotEqual(key, AzureMeetingProvider.StaticCacheKey("other"));
        // The answer deployment, when set, is the one the key belongs to.
        using var answerDeployment = new CaptureHandler();
        await DrainAnswerAsync(answerDeployment, new ServiceSettings
            { ChatDeployment = "test", AnswerDeployment = "answers", ChatPromptCacheKey = "static" }, Scoped);
        Assert.Equal(AzureMeetingProvider.StaticCacheKey("answers"), CacheKey(answerDeployment.RequestBody!));
        var (classifier, translation) = await AuxiliaryBodiesAsync(settings);
        Assert.DoesNotContain("prompt_cache_key", classifier);
        Assert.DoesNotContain("prompt_cache_key", translation);
    }

    [Fact]
    public async Task ASessionPromptCacheKeyIsTheSessionsOwnRandomScope()
    {
        var settings = new ServiceSettings { ChatDeployment = "test", ChatPromptCacheKey = "session" };
        using var scoped = new CaptureHandler();
        await DrainAnswerAsync(scoped, settings, Scoped);
        Assert.Equal("answer-" + Scoped.CacheScope, CacheKey(scoped.RequestBody!));
        // Without a scope (never the case for a live session) nothing is sent rather than a key shared by every session.
        using var unscoped = new CaptureHandler();
        await DrainAnswerAsync(unscoped, settings);
        Assert.DoesNotContain("prompt_cache_key", unscoped.RequestBody);
        var (classifier, translation) = await AuxiliaryBodiesAsync(settings);
        Assert.DoesNotContain("prompt_cache_key", classifier);
        Assert.DoesNotContain("prompt_cache_key", translation);
    }

    [Theory]
    [InlineData("grounded")]
    [InlineData("unavailable")]
    public async Task RouteInFocusMovesOnlyTheRouteAndTheSearchFailureInstructions(string status)
    {
        var grounding = status == "grounded"
            ? new Grounding("grounded", [new("JMAP supports push updates.", new("Notes", "https://example.test/notes", null))])
            : new Grounding("unavailable", []);
        using var before = new CaptureHandler();
        await DrainAnswerAsync(before, new ServiceSettings { ChatDeployment = "test" }, Scoped, grounding);
        using var after = new CaptureHandler();
        await DrainAnswerAsync(after, new ServiceSettings { ChatDeployment = "test", RouteInFocus = true }, Scoped, grounding);
        var off = ParticipantNotesTests.Messages(before.RequestBody!);
        var on = ParticipantNotesTests.Messages(after.RequestBody!);

        const string route = "\"responseRoute\":\"knowledge\"";
        int Index(IReadOnlyList<(string Role, string Content)> messages, string prefix) =>
            messages.Select((message, index) => (message, index)).Single(item => item.message.Content.StartsWith(prefix, StringComparison.Ordinal)).index;
        var context = Index(off, "Untrusted session context");
        var focus = Index(off, "Untrusted response focus");
        Assert.Contains(route + ",", off[context].Content);
        Assert.DoesNotContain("responseRoute", off[focus].Content);
        Assert.Equal(off[context].Content.Replace(route + ",", ""), on[Index(on, "Untrusted session context")].Content);
        Assert.Equal(off[focus].Content.Replace("{\"Labels\"", "{" + route + ",\"Labels\""), on[Index(on, "Untrusted response focus")].Content);

        var unavailable = off.Where(message => message.Content == AzureMeetingProvider.UnavailableInstructions).ToArray();
        if (status == "unavailable")
        {
            // Off: the third system message, as before. On: the last message, after the evidence.
            Assert.Equal(("system", AzureMeetingProvider.UnavailableInstructions), off[2]);
            Assert.Equal(("system", AzureMeetingProvider.UnavailableInstructions), on[^1]);
            Assert.StartsWith("Untrusted retrieved evidence", on[^2].Content);
            Assert.Single(unavailable);
        }
        else Assert.Empty(unavailable);
        Assert.Equal(off.Length, on.Length);
        Assert.Equal(off.Where(message => !message.Content.StartsWith("Untrusted", StringComparison.Ordinal)).Order(),
            on.Where(message => !message.Content.StartsWith("Untrusted", StringComparison.Ordinal)).Order());
    }

    /// <summary>A session as the server starts it: with its own random cache scope.</summary>
    private static readonly SessionOptions Scoped = SessionOptions.Legacy with { CacheScope = "0f1e2d3c4b5a69788796a5b4c3d2e1f0" };

    private static string CacheKey(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("prompt_cache_key").GetString()!;
    }

    private static async Task<(string Classifier, string Translation)> AuxiliaryBodiesAsync(ServiceSettings settings)
    {
        string Completion(string text) => JsonSerializer.Serialize(new
        {
            id = "test", @object = "chat.completion", created = 1, model = "test",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } }
        });
        using var classifier = new CaptureHandler { ResponseBody = Completion("RESPOND"), ContentType = "application/json" };
        await Provider(classifier, settings).ShouldRespondAsync([new("Any questions?")], CancellationToken.None);
        using var translation = new CaptureHandler { ResponseBody = Completion("좋습니다."), ContentType = "application/json" };
        await Provider(translation, settings).TranslateReplyAsync("Sounds good.", TranslationContext.Empty, CancellationToken.None);
        return (classifier.RequestBody!, translation.RequestBody!);
    }

    private static AzureMeetingProvider Provider(CaptureHandler handler, ServiceSettings settings)
    {
        var http = new HttpClient(handler, disposeHandler: false);
        var client = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only-key"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        return new AzureMeetingProvider(settings, client, null);
    }

    private static async Task DrainAnswerAsync(CaptureHandler handler, ServiceSettings settings, SessionOptions? options = null,
        Grounding? grounding = null)
    {
        await foreach (var _ in Provider(handler, settings).AnswerWithSuggestionsAsync([new("How does it help?")],
            grounding ?? new("disabled", []), options ?? SessionOptions.Legacy, "knowledge", CancellationToken.None)) { }
    }

    private static MeterListener ListenToTokens(List<(long, string, string)> tokens)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == MeetingMetrics.MeterName && instrument.Name == "voiceassistant.chat.tokens")
                    owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var list = tags.ToArray();
            string Tag(string key) => list.Single(tag => tag.Key == key).Value as string ?? "";
            Assert.Equal(2, list.Length);
            lock (tokens) tokens.Add((value, Tag("operation"), Tag("type")));
        });
        listener.Start();
        return listener;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string ResponseBody { get; init; } = "data: [DONE]\n\n";
        public string ContentType { get; init; } = "text/event-stream";
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests++;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, ContentType)
            };
        }
    }
}
