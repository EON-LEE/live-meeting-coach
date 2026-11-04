using System.Text;
using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    private const string MalformedFeedback = """{"type":"response.feedback","responseId":"x","reason":"meh"}""";

    private static string Feedback(string responseId, string reason) =>
        JsonSerializer.Serialize(new { type = "response.feedback", responseId, reason });

    private static string Viewed(params (string ResponseId, int VisibleMs)[] items) =>
        JsonSerializer.Serialize(new { type = "response.viewed", items = items.Select(item => new { responseId = item.ResponseId, visibleMs = item.VisibleMs }) });

    [Fact]
    public async Task ReadyAdvertisesFeedbackAndViewedExceptForTranscribeOnlySessions()
    {
        await using (var host = await Host.StartAsync())
        using (var socket = await host.ConnectAsync())
        {
            await Send(socket, Start);
            var ready = await Receive(socket);
            Assert.True(ready.GetProperty("feedback").GetBoolean());
            Assert.True(ready.GetProperty("viewed").GetBoolean());
            Assert.Equal(["type", "protocolVersion", "sessionUpdate", "feedback", "viewed"], ready.EnumerateObject().Select(property => property.Name));
        }
        await using var transcribeHost = await Host.StartAsync();
        using var transcribeOnly = await transcribeHost.ConnectAsync();
        await Send(transcribeOnly, WithOptions(new { transcribeOnly = true, endSilenceMs = 500 }));
        var transcribeReady = await Receive(transcribeOnly);
        Assert.False(transcribeReady.TryGetProperty("feedback", out _));
        Assert.False(transcribeReady.TryGetProperty("viewed", out _));
    }

    [Fact]
    public async Task FeedbackAndVisibilityOfAStartedReplyAreCountedWithoutContentOrAcknowledgement()
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded" }));
        await Receive(socket);
        provider.Emit(new("q", 1, "What is the next step for the rollout?", true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        var responseId = messages.Single(message => TypeOf(message) == "response.started").GetProperty("responseId").GetString()!;

        await Send(socket, Feedback(responseId, "hard_to_read"));
        await Send(socket, Viewed((responseId, 4200), ("not-a-reply-of-this-session", 900)));
        await Send(socket, Feedback("not-a-reply-of-this-session", "wrong"));
        // Nothing acknowledges a report: the next event is the error of this malformed one, handled in order after them.
        await Send(socket, MalformedFeedback);
        var error = await Receive(socket);

        Assert.Equal("invalid_feedback", error.GetProperty("code").GetString());
        var feedback = Assert.Single(capture.Named("voiceassistant.reply.feedback"));
        Assert.Equal([new("reason", "hard_to_read")], feedback.Tags);
        var visible = Assert.Single(capture.Named("voiceassistant.reply.visible"));
        Assert.Equal(4200, visible.Value);
        Assert.Empty(visible.Tags);
        var line = Assert.Single(logs.Messages, message => message.Contains("ReplyFeedback", StringComparison.Ordinal));
        Assert.Equal("Information: ReplyFeedback reason=hard_to_read", line);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(responseId, StringComparison.Ordinal));
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Theory]
    [InlineData("""{"type":"response.feedback","reason":"good"}""", "invalid_feedback")]
    [InlineData("""{"type":"response.feedback","responseId":"","reason":"good"}""", "invalid_feedback")]
    [InlineData("""{"type":"response.feedback","responseId":7,"reason":"good"}""", "invalid_feedback")]
    [InlineData("""{"type":"response.feedback","responseId":"r"}""", "invalid_feedback")]
    [InlineData("""{"type":"response.feedback","responseId":"r","reason":"GOOD"}""", "invalid_feedback")]
    [InlineData("""{"type":"response.viewed"}""", "invalid_viewed")]
    [InlineData("""{"type":"response.viewed","items":[]}""", "invalid_viewed")]
    [InlineData("""{"type":"response.viewed","items":{"responseId":"r","visibleMs":1}}""", "invalid_viewed")]
    [InlineData("""{"type":"response.viewed","items":[{"responseId":"r","visibleMs":-1}]}""", "invalid_viewed")]
    [InlineData("""{"type":"response.viewed","items":[{"responseId":"r","visibleMs":3600001}]}""", "invalid_viewed")]
    [InlineData("""{"type":"response.viewed","items":[{"responseId":"r","visibleMs":1.5}]}""", "invalid_viewed")]
    [InlineData("""{"type":"response.viewed","items":[{"responseId":"r","visibleMs":"5"}]}""", "invalid_viewed")]
    [InlineData("""{"type":"response.viewed","items":[{"visibleMs":5}]}""", "invalid_viewed")]
    public async Task AMalformedReportIsANonFatalErrorAndTheSessionGoesOn(string command, string code)
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded" }));
        await Receive(socket);

        await Send(socket, command);
        var error = await Receive(socket);
        Assert.Equal("error", TypeOf(error));
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());

        provider.Emit(new("q", 1, "What is the next step for the rollout?", true));
        Assert.Contains(await ReceiveUntilAsync(socket, Completed), message => TypeOf(message) == "response.completed");
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ATranscribeOnlySessionRejectsReportsWithoutEnding()
    {
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { transcribeOnly = true, endSilenceMs = 500 }));
        await Receive(socket);

        foreach (var command in new[] { Feedback("r", "good"), Viewed(("r", 10)) })
        {
            await Send(socket, command);
            var error = await Until(socket, "error");
            Assert.Equal("transcribe_only", error.GetProperty("code").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
        }
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public void ReportsParseIntoTheirParts()
    {
        var feedback = SessionWire.ParseCommand(Encoding.UTF8.GetBytes(Feedback("r1", "unnecessary")));
        Assert.Equal(SessionEventKind.ResponseFeedback, feedback.Kind);
        Assert.Equal(new ReplyFeedback("r1", "unnecessary"), feedback.Value);
        foreach (var reason in SessionWire.FeedbackReasons)
            Assert.Equal(reason, Assert.IsType<ReplyFeedback>(SessionWire.ParseCommand(Encoding.UTF8.GetBytes(Feedback("r", reason))).Value).Reason);
        Assert.Equal(["good", "late", "wrong", "thin", "hard_to_read", "unnecessary", "missing"], SessionWire.FeedbackReasons);

        var bounds = SessionWire.ParseCommand(Encoding.UTF8.GetBytes(Viewed(("r1", 0), ("r2", SessionWire.MaxVisibleMs))));
        Assert.Equal(SessionEventKind.ResponseViewed, bounds.Kind);
        Assert.Equal([new ReplyViewed("r1", 0), new ReplyViewed("r2", 3_600_000)], Assert.IsType<ReplyViewed[]>(bounds.Value));
        var twenty = Enumerable.Range(0, 20).Select(index => ($"r{index}", index)).ToArray();
        Assert.Equal(20, Assert.IsType<ReplyViewed[]>(SessionWire.ParseCommand(Encoding.UTF8.GetBytes(Viewed(twenty))).Value).Length);
        var tooMany = Enumerable.Range(0, 21).Select(index => ($"r{index}", index)).ToArray();
        Assert.Null(SessionWire.ParseCommand(Encoding.UTF8.GetBytes(Viewed(tooMany))).Value);
    }

    [Fact]
    public void OnlyTheLastTwentyStartedRepliesAreKnown()
    {
        var recent = new RecentResponses();
        for (var index = 0; index < 21; index++) recent.Add($"r{index}");
        Assert.False(recent.Contains("r0"));
        Assert.True(recent.Contains("r1"));
        Assert.True(recent.Contains("r20"));
        Assert.False(recent.Contains("r21"));
    }
}
