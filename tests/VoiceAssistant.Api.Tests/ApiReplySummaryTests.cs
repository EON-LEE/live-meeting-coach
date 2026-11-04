using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    // Every value the per-reply summary line may hold: bounded words and numbers, never text, ids or users.
    private static readonly Regex SummaryAllowList = new(
        @"^Information: ReplySummary outcome=(completed|fallback|cancelled|error) " +
        @"start_reason=(manual|no_settle|early_final|settle|supersede|quiet) classifier=(respond|listen|timeout|failed|none) " +
        @"decision=(respond|listen|unknown|none) waits=\d+ quiet_armed=(true|false) queued=(true|false) queue_wait_ms=(-1|\d+) " +
        @"speculation=(none|adopted|discarded_mismatch|discarded_target_changed|discarded_cancelled) " +
        @"spec_edit=(0|1|2-3|4-8|9\+|over_cap|none) speech_restarts=\d+ first_line_ms=(-1|\d+)$");

    private const string SummaryQuestion = "What is the next step for the rollout?";
    private const string SummaryTurn = "turn-5c1d";

    private static string[] Summaries(CollectingLogs logs) =>
        logs.Messages.Where(message => message.Contains("ReplySummary", StringComparison.Ordinal)).ToArray();

    /// <summary>Waits for <paramref name="count"/> summary lines; each must match the allow-list.</summary>
    private static async Task<string[]> SummariesAsync(CollectingLogs logs, int count)
    {
        await WaitUntilAsync(() => Summaries(logs).Length >= count, TimeSpan.FromSeconds(5));
        var lines = Summaries(logs);
        Assert.Equal(count, lines.Length);
        Assert.All(lines, line => Assert.Matches(SummaryAllowList, line));
        return lines;
    }

    private static string Summary(string outcome, string startReason, string classifier = "none", string decision = "none",
        int waits = 0, bool quietArmed = false, int queueWaitMs = -1, string speculation = "none", string specEdit = "none",
        string firstLineMs = "-1") =>
        $"Information: ReplySummary outcome={outcome} start_reason={startReason} classifier={classifier} decision={decision} " +
        $"waits={waits} quiet_armed={(quietArmed ? "true" : "false")} queued={(queueWaitMs >= 0 ? "true" : "false")} " +
        $"queue_wait_ms={queueWaitMs} speculation={speculation} spec_edit={specEdit} speech_restarts=0 first_line_ms={firstLineMs}";

    private static IEnumerable<KeyValuePair<string, object?>[]> Outcomes(MetricCapture capture) =>
        capture.Named("voiceassistant.reply.outcomes").Select(measurement => measurement.Tags);

    private static KeyValuePair<string, object?>[] OutcomeTags(string startReason, string classifier, string outcome) =>
        [new("start_reason", startReason), new("classifier", classifier), new("outcome", outcome)];

    private static async Task StopAsync(ClientWebSocket socket)
    {
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
    }

    [Fact]
    public async Task ACompletedReplyLogsOneContentFreeSummaryAndCountsItsOutcome()
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded" }));
        await Receive(socket);

        provider.Emit(new(SummaryTurn, 1, SummaryQuestion, true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        await StopAsync(socket);

        var line = Assert.Single(await SummariesAsync(logs, 1));
        Assert.Matches(new Regex("^" + Regex.Escape(Summary("completed", "no_settle", "respond", "respond", firstLineMs: "X"))
            .Replace("X", @"\d+") + "$"), line);
        var responseId = messages.Single(message => TypeOf(message) == "response.started").GetProperty("responseId").GetString()!;
        foreach (var secret in new[] { responseId, SummaryTurn, SummaryQuestion, "concise response", ObjectId })
            Assert.DoesNotContain(logs.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.Equal(OutcomeTags("no_settle", "respond", "completed"), Assert.Single(Outcomes(capture)));
        // Only a reply that waited in the queue records how long.
        Assert.Empty(capture.Named("voiceassistant.reply.queue_wait"));
    }

    [Fact]
    public async Task ACancelledReplyLogsItsSummaryOnce()
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);

        provider.Emit(new(SummaryTurn, 1, SummaryQuestion, true));
        await Until(socket, "response.started");
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Send(socket, """{"type":"response.cancel"}""");
        await Until(socket, "response.cancelled");
        await StopAsync(socket);

        // Cancelled before its first line and before its classifier was asked.
        Assert.Equal([Summary("cancelled", "no_settle")], await SummariesAsync(logs, 1));
        Assert.Equal(OutcomeTags("no_settle", "none", "cancelled"), Assert.Single(Outcomes(capture)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReplyStillGeneratingWhenTheSessionEndsLogsItsSummaryOnce(bool clientCloses)
    {
        var logs = new CollectingLogs();
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);

        provider.Emit(new(SummaryTurn, 1, SummaryQuestion, true));
        await Until(socket, "response.started");
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (clientCloses)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        }
        else await StopAsync(socket);

        Assert.Equal([Summary("cancelled", "no_settle")], await SummariesAsync(logs, 1));
        // Nothing else is logged for it later.
        await Task.Delay(100);
        Assert.Single(Summaries(logs));
    }

    [Fact]
    public async Task AFailedReplyLogsAnErrorSummaryAndTheQueuedReplyItsOwn()
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        var provider = new ControlledProvider { AutomaticRetrieval = true, FailFirstAnswer = true };
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);

        provider.Emit(new("one", 1, "What is our delivery plan?", true));
        await provider.FirstAnswerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Emit(new("two", 1, "Who owns the launch decision?", true));
        await Until(socket, "transcript.final");
        provider.FirstAnswerRelease.SetResult();
        Assert.Equal("provider_failed", (await Until(socket, "error")).GetProperty("code").GetString());
        await Until(socket, "response.completed");
        await StopAsync(socket);

        var lines = await SummariesAsync(logs, 2);
        Assert.Equal(Summary("error", "no_settle"), lines[0]);
        Assert.Matches(@" outcome=completed start_reason=no_settle classifier=respond decision=respond waits=0 quiet_armed=false queued=true queue_wait_ms=\d+ ", lines[1]);
        Assert.Equal([OutcomeTags("no_settle", "none", "error"), OutcomeTags("no_settle", "respond", "completed")], Outcomes(capture));
        Assert.Single(capture.Named("voiceassistant.reply.queue_wait"));
    }

    [Fact]
    public async Task AReplyWithoutItsMaterialsOrTheModelLogsAFallbackSummary()
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        await using var host = await Host.StartAsync(provider: new UnavailableSearchProvider(), logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);

        await socket.SendAsync(Enumerable.Repeat((byte)1, 640).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("unavailable", (await ReceiveUntilAsync(socket, Completed))[^1].GetProperty("grounding").GetString());
        await StopAsync(socket);

        Assert.Equal([Summary("fallback", "no_settle")], await SummariesAsync(logs, 1));
        Assert.Equal(OutcomeTags("no_settle", "none", "fallback"), Assert.Single(Outcomes(capture)));
    }

    [Fact]
    public async Task AQueuedReplyReportsHowLongItWaitedBehindTheActiveOne()
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        var clock = new ManualClock();
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider, logs: logs, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);

        provider.Emit(new("one", 1, "What is our delivery plan?", true));
        await Until(socket, "response.started");
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Emit(new("two", 1, "Who owns the launch decision?", true));
        await Until(socket, "transcript.final");
        clock.Advance(TimeSpan.FromMilliseconds(300));
        provider.AutomaticRetrieval = true;
        provider.Release.SetResult(new("no_matches", []));
        await Until(socket, "response.completed");
        await Until(socket, "response.completed");
        await StopAsync(socket);

        var lines = await SummariesAsync(logs, 2);
        Assert.Equal(Summary("completed", "no_settle", "respond", "respond", firstLineMs: "300"), lines[0]);
        Assert.Equal(Summary("completed", "no_settle", "respond", "respond", queueWaitMs: 300, firstLineMs: "300"), lines[1]);
        var wait = Assert.Single(capture.Named("voiceassistant.reply.queue_wait"));
        Assert.Equal(300, wait.Value);
        Assert.Equal(["provider", "trigger", "route", "response_mode"], wait.Tags.Select(tag => tag.Key));
        Assert.Contains(new KeyValuePair<string, object?>("trigger", "automatic"), wait.Tags);
    }

    [Fact]
    public async Task AReplyReplacedByARequestedOneLogsItsCancelledSummaryAfterTheNewOneStarts()
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);

        provider.Emit(new(SummaryTurn, 1, SummaryQuestion, true));
        await Until(socket, "response.started");
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.AutomaticRetrieval = true;
        await Send(socket, """{"type":"response.request"}""");
        await Until(socket, "response.cancelled");
        await Until(socket, "response.completed");
        await StopAsync(socket);

        var lines = await SummariesAsync(logs, 2);
        Assert.Equal(Summary("cancelled", "no_settle"), lines[0]);
        Assert.Matches(" outcome=completed start_reason=manual classifier=respond decision=respond waits=0 ", lines[1]);
        Assert.Equal([OutcomeTags("no_settle", "none", "cancelled"), OutcomeTags("manual", "respond", "completed")], Outcomes(capture));
    }

    [Fact]
    public async Task AQuietReplyReportsTheSettlesItWaitedAndItsArmedQuietTimer()
    {
        var logs = new CollectingLogs();
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: QuietReplyOn(), logs: logs);
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("clarified", 1, ClarifiedAnswer, true));
        await Until(socket, "transcript.final");
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        await Until(socket, "response.completed");
        await StopAsync(socket);

        // A statement: the classifier says listen, and that was known when its first line was sent.
        Assert.Equal([Summary("completed", "quiet", "listen", "listen", waits: 1, quietArmed: true, firstLineMs: "2500")],
            await SummariesAsync(logs, 1));
    }

    [Fact]
    public async Task AnAdoptedSpeculativeReplyLogsOneSummaryAndADiscardedOneNone()
    {
        var logs = new CollectingLogs();
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn(), logs: logs);
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);

        // The first speculation is discarded by newer words, the second adopted by its final.
        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.FirstLines));
        await PauseOnPartialAsync(socket, provider, clock, new("q", 2, RolloutPartial + " for the launch", false));
        Assert.Equal(RolloutPartial + " for the launch", await NextAsync(provider.FirstLines));
        provider.Emit(new("q", 3, "What is the rollout plan for the launch?", true));
        await ReceiveUntilAsync(socket, Completed);
        await StopAsync(socket);

        Assert.Matches(" start_reason=early_final .* speculation=adopted spec_edit=0 ", Assert.Single(await SummariesAsync(logs, 1)));
    }

    [Fact]
    public async Task AReplyToAFinalWithOtherWordsReportsTheDiscardedSpeculationAndHowFarItsWordsWere()
    {
        var logs = new CollectingLogs();
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn(), logs: logs);
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        provider.Emit(new("q", 2, "What is the rollout plan for Q3?", true));
        await ReceiveUntilAsync(socket, Completed);
        await StopAsync(socket);

        // "what is the rollout plan" and "what is the rollout plan for q3" are seven edits apart.
        Assert.Matches(" start_reason=early_final .* speculation=discarded_mismatch spec_edit=4-8 ",
            Assert.Single(await SummariesAsync(logs, 1)));
    }

    [Theory]
    [InlineData("discarded_mismatch", "rollout plan", "rollout plan", "0")]
    [InlineData("discarded_mismatch", "rollout plan", "rollout plans", "1")]
    [InlineData("discarded_mismatch", "rollout plan", "rollout plan q3", "2-3")]
    [InlineData("discarded_mismatch", "rollout plan", "rollout", "4-8")]
    [InlineData("discarded_mismatch", "rollout plan", "launch", "9+")]
    [InlineData("discarded_target_changed", null, null, "none")]
    [InlineData("adopted", null, null, "0")]
    [InlineData("none", null, null, "none")]
    public void TheSummaryBucketsHowFarTheFinalsWordsWereFromTheSpeculatedOnes(string outcome, string? speculated, string? final, string bucket)
    {
        var logs = new CollectingLogs();
        var measurement = new MeetingMetrics.ResponseMeasurement(0, "Fake", manual: false);
        measurement.SpeculationEnded(outcome, speculated, final);
        measurement.Summarize(logs.CreateLogger("test"), "completed", speechRestarts: 0);
        Assert.Contains($" spec_edit={bucket} ", Assert.Single(logs.Messages));
    }

    [Fact]
    public void WordsLongerThanTheCapAreNotCompared()
    {
        var logs = new CollectingLogs();
        var measurement = new MeetingMetrics.ResponseMeasurement(0, "Fake", manual: false);
        measurement.SpeculationEnded("discarded_mismatch", new string('a', 257), "a");
        measurement.Summarize(logs.CreateLogger("test"), "completed", speechRestarts: 2);
        Assert.Matches(" spec_edit=over_cap speech_restarts=2 ", Assert.Single(logs.Messages));
        Assert.Equal(3, MeetingMetrics.ResponseMeasurement.EditDistance("kitten", "sitting"));
        Assert.Equal(3, MeetingMetrics.ResponseMeasurement.EditDistance("", "abc"));
        Assert.Equal(0, MeetingMetrics.ResponseMeasurement.EditDistance("same", "same"));
    }

    [Fact]
    public void ASpeculativeReplyLogsItsSummaryOnlyOnceAdoptedAndADiscardedOneNever()
    {
        var logs = new CollectingLogs();
        var logger = logs.CreateLogger("test");
        var discarded = new MeetingMetrics.ResponseMeasurement(0, "Fake", manual: false) { Speculative = true };
        discarded.Summarize(logger, "completed", 0);
        discarded.Discard();
        var adopted = new MeetingMetrics.ResponseMeasurement(0, "Fake", manual: false) { Speculative = true };
        adopted.Began(new ReplyCause("early_final"));
        adopted.Summarize(logger, "completed", 0);
        adopted.Summarize(logger, "cancelled", 0);
        Assert.Empty(logs.Messages);

        adopted.Adopt(0);
        Assert.Matches(SummaryAllowList, Assert.Single(logs.Messages));
        Assert.Contains("outcome=completed", logs.Messages.Single());
    }

    [Fact]
    public async Task TheFakeListenTopicHearsAStatementTheClassifierCallsListen()
    {
        Assert.Equal(ReplyDecision.Reply, ReplyTrigger.Evaluate([FakeMeetingProvider.StatementText]));
        Assert.False(ReplyTrigger.ContainsQuestion([FakeMeetingProvider.StatementText]));
        foreach (var (topic, transcript, decision) in new[]
                 {
                     (FakeMeetingProvider.ListenTopic, FakeMeetingProvider.StatementText, "listen"),
                     ("", FakeMeetingProvider.TranscriptText, "respond")
                 })
        {
            foreach (var settleMs in new[] { 0, 1000 })
            {
                await using var host = await Host.StartAsync();
                using var socket = await host.ConnectAsync();
                await Send(socket, WithOptions(new { topic, replySettleMs = settleMs }));
                Assert.Equal("session.ready", TypeOf(await Receive(socket)));
                await socket.SendAsync(Enumerable.Repeat((byte)1, 640).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);

                var messages = await ReceiveUntilAsync(socket, Completed);
                Assert.Equal(transcript, messages.Single(message => TypeOf(message) == "transcript.final").GetProperty("text").GetString());
                var primaryDone = messages.Single(message => TypeOf(message) == "response.delta" && message.TryGetProperty("primaryDone", out _));
                Assert.Equal(decision, primaryDone.GetProperty("decision").GetString());
                Assert.Equal(decision == "respond", messages[^1].GetProperty("respondNow").GetBoolean());
                await StopAsync(socket);
            }
        }
    }
}
