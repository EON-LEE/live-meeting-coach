using System.Text.Json;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class ReasoningEffortTests
{
    private static readonly ConversationTurn[] Question = [new("What is the delivery plan for the rollout?")];

    private static IConfiguration Configuration(string key, string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)]).Build();

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("minimal", "minimal")]
    [InlineData(" Low ", "low")]
    [InlineData("MEDIUM", "medium")]
    public void ReasoningEffortIsUnsetOrOneOfTheSupportedValues(string? value, string? expected) =>
        Assert.Equal(expected, ServiceSettings.ReadReasoningEffort(Configuration("Azure:ChatReasoningEffort", value), "Azure:ChatReasoningEffort"));

    [Theory]
    [InlineData("high")]
    [InlineData("none")]
    [InlineData("fast")]
    public void AnUnsupportedReasoningEffortPreventsStartupInsteadOfBeingIgnored(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServiceSettings.ReadReasoningEffort(Configuration("Azure:ChatReasoningEffort", value), "Azure:ChatReasoningEffort"));
        Assert.Contains("Azure:ChatReasoningEffort", failure.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" meeting-answer ", "meeting-answer")]
    [InlineData("gpt-5.6_terra", "gpt-5.6_terra")]
    public void TheAnswerDeploymentIsUnsetOrADeploymentName(string? value, string? expected) =>
        Assert.Equal(expected, ServiceSettings.ReadDeploymentName(Configuration("Azure:AnswerDeployment", value), "Azure:AnswerDeployment"));

    [Theory]
    [InlineData("meeting answer")]
    [InlineData("../chat")]
    [InlineData("-answer")]
    [InlineData("answer?api-version=1")]
    public void AMalformedAnswerDeploymentPreventsStartup(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServiceSettings.ReadDeploymentName(Configuration("Azure:AnswerDeployment", value), "Azure:AnswerDeployment"));
        Assert.Contains("Azure:AnswerDeployment", failure.Message);
    }

    [Fact]
    public async Task OnlyTheAnswerGoesToTheAnswerDeploymentWithTheAnswerEffort()
    {
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http, new ServiceSettings
        {
            ChatDeployment = "chat", AnswerDeployment = "answer", EmbeddingDeployment = "embedding", ChatReasoningEffort = "low"
        });

        await AnswerAsync(provider);
        await provider.ShouldRespondAsync(Question, CancellationToken.None);
        await provider.TranslateReplyAsync("Let's check the next step.", TranslationContext.Empty, CancellationToken.None);

        var calls = transport.ChatBodies.Zip(transport.ChatPaths, (body, path) =>
        {
            using var json = JsonDocument.Parse(body);
            var streamed = json.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            var effort = json.RootElement.TryGetProperty("reasoning_effort", out var value) ? value.GetString() : null;
            return (Streamed: streamed, Path: path, Effort: effort);
        }).ToArray();
        var answer = Assert.Single(calls, call => call.Streamed);
        Assert.Contains("/deployments/answer/", answer.Path);
        Assert.Equal("low", answer.Effort);
        // The turn classifier and the Korean translation (tried twice, because the fake does not answer in Korean) stay on the
        // chat deployment, without the answer's effort.
        var auxiliary = calls.Where(call => !call.Streamed).ToArray();
        Assert.True(auxiliary.Length >= 2);
        Assert.All(auxiliary, call =>
        {
            Assert.Contains("/deployments/chat/", call.Path);
            Assert.Null(call.Effort);
        });
    }

    [Fact]
    public async Task WithoutAnAnswerDeploymentTheAnswerUsesTheChatDeployment()
    {
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http);

        await AnswerAsync(provider);

        Assert.Contains("/deployments/chat/", Assert.Single(transport.ChatPaths));
    }

    [Fact]
    public async Task UnsetEffortsSendNoReasoningEffortAtAll()
    {
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http);

        await AnswerAsync(provider);
        await provider.ShouldRespondAsync(Question, CancellationToken.None);

        Assert.NotEmpty(transport.ChatBodies);
        Assert.All(transport.ChatBodies, body => Assert.DoesNotContain("reasoning_effort", body));
    }

    private static async Task AnswerAsync(AzureMeetingProvider provider)
    {
        await foreach (var _ in provider.AnswerWithSuggestionsAsync(Question, new Grounding("disabled", []), SessionOptions.Legacy,
            "transcript", CancellationToken.None)) { }
    }
}

public sealed partial class ApiTests
{
    [Theory]
    [InlineData("Azure:ChatReasoningEffort", "high")]
    [InlineData("Azure:AnswerDeployment", "bad name")]
    [InlineData("Speech:MaterialPhraseHints", "on")]
    public async Task AnUnsupportedLiveChatSwitchValueStopsTheAppFromStarting(string key, string value) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(azure: true, configuration: new() { [key] = value }));
}
