using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

// Exact reply timing and the non-fatal error codes, pinned before the session is restructured.
public sealed partial class ApiTests
{
    /// <summary>
    /// Everything the session sent before it handled a settings change sent now. A due settle is queued the moment the clock
    /// fires it, so a reply it started is among these messages. The change keeps the response mode, so it changes nothing else.
    /// </summary>
    private static async Task<List<JsonElement>> SentBeforeProbeAsync(ClientWebSocket socket, string responseMode = "grounded")
    {
        await Send(socket, UpdateCommand(new { responseMode }));
        return await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "session.updated"));
    }

    [Fact]
    public async Task ACompleteQuestionIsAnsweredAfterExactlyThreeHundredMilliseconds()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("q", 1, RolloutQuestion, true));
        Assert.Equal("q", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.DoesNotContain(await SentBeforeProbeAsync(socket), message => TypeOf(message) == "response.started");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("q", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
        Assert.Equal(1, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AQuestionWaitingMoreThanTwelveSecondsIsNotPostponedByOngoingSpeech()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("q", 1, RolloutQuestion, true));
        Assert.Equal("q", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(++timers);
        // Someone keeps talking without a pause, a partial every 250 ms. Up to and including 12.000 s each one re-arms the
        // full 1000 ms window; after that the waiting question is no longer postponed, so the window armed at 12.000 s ends it.
        int? startedAtStep = null;
        for (var step = 1; step <= 56 && startedAtStep is null; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            provider.Emit(new("talk", step, "we keep going over the budget " + step, false));
            var sent = await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "transcript.partial"));
            if (sent.Any(message => TypeOf(message) == "response.started")) startedAtStep = step;
            else if (step <= 48) await clock.WaitForTimersAsync(++timers);
        }

        Assert.Equal(52, startedAtStep);
        await ReceiveUntilAsync(socket, Completed);
        Assert.Equal([RolloutQuestion], Targets(provider));
        Assert.Equal(1, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ARequestBeforeAnyFinalIsARetryableNoTranscriptErrorAndTheSessionContinues()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);

        await Send(socket, """{"type":"response.request"}""");
        var error = await Until(socket, "error");
        Assert.Equal("no_transcript", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());

        provider.Emit(new("statement", 1, MigrationStatement, true));
        await Until(socket, "transcript.final");
        await Send(socket, """{"type":"response.request"}""");
        Assert.Equal("statement", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
        Assert.Equal(1, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AnUnexpectedModelFailureIsARetryableResponseUnavailableErrorAndTheSessionContinues()
    {
        var provider = new FailingAnswerProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        provider.Emit(new("q", 1, RolloutQuestion, true));
        var sent = await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "response.cancelled"));
        var error = Assert.Single(sent, message => TypeOf(message) == "error");
        Assert.Equal("response_unavailable", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Contains(sent, message => TypeOf(message) == "response.started");

        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) == "error");
    }

    private sealed class FailingAnswerProvider : IMeetingProvider
    {
        public Action<Transcript> Emit { get; private set; } = _ => throw new InvalidOperationException("Speech is not started.");
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation)
        {
            Emit = transcript;
            return new FakeMeetingProvider().StartSpeechAsync(_ => { }, error, cancellation);
        }
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
            Task.FromResult(new Grounding("no_matches", []));
        public async IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            await Task.Yield();
            throw new InvalidOperationException("Synthetic model failure.");
#pragma warning disable CS0162 // An iterator needs a yield.
            yield break;
#pragma warning restore CS0162
        }
    }
}
