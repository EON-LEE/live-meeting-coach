using System.Runtime.CompilerServices;
using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

/// <summary>
/// Error codes that only a session driven without a real network can reach on demand: an idle client, a client that stops
/// reading, and a request the host aborts. The session runs on a <see cref="ScriptedSocket"/>, on the thread pool as under the
/// server rather than on the test framework's synchronization context.
/// </summary>
public sealed class SessionErrorCodeTests
{
    private const string Start = """{"type":"session.start","protocolVersion":1,"audio":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1}}""";

    [Fact]
    public async Task AClientThatSendsNothingEndsWithARetryableIdleTimeout()
    {
        var socket = new ScriptedSocket();
        var session = new MeetingSession(socket, new QuietProvider(), "user", new ListLogger<MeetingSession>())
            { IdleTimeout = TimeSpan.FromMilliseconds(200) };
        socket.Client(Start);
        var run = Task.Run(() => session.RunAsync(CancellationToken.None));

        Assert.Equal("session.ready", Type(await socket.NextAsync()));
        var error = await socket.NextAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("idle_timeout", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task AClientThatStopsReadingEndsWithARetryableOverload()
    {
        var socket = new ScriptedSocket();
        var provider = new QuietProvider();
        var session = new MeetingSession(socket, provider, "user", new ListLogger<MeetingSession>());
        socket.Client(Start);
        var run = Task.Run(() => session.RunAsync(CancellationToken.None));
        Assert.Equal("session.ready", Type(await socket.NextAsync()));

        // The event loop waits on its first send while recognition keeps producing: the bounded queue (64) overflows.
        socket.HoldSends();
        for (var revision = 1; revision <= 80; revision++) provider.Emit(new("turn", revision, "We are still talking", false));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var error = Assert.Single(socket.Sent, message => Type(message) == "error");
        Assert.Equal("session_overloaded", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task ARequestTheHostAbortsEndsWithARetryableSessionEnded()
    {
        var socket = new ScriptedSocket();
        var session = new MeetingSession(socket, new QuietProvider(), "user", new ListLogger<MeetingSession>());
        using var aborted = new CancellationTokenSource();
        socket.Client(Start);
        var run = Task.Run(() => session.RunAsync(aborted.Token));
        Assert.Equal("session.ready", Type(await socket.NextAsync()));

        aborted.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var error = Assert.Single(socket.Sent, message => Type(message) == "error");
        Assert.Equal("session_ended", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    private static string? Type(JsonElement message) => message.GetProperty("type").GetString();

    private sealed class QuietProvider : IMeetingProvider
    {
        public Action<Transcript> Emit { get; private set; } = _ => throw new InvalidOperationException("Speech is not started.");
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation)
        {
            Emit = transcript;
            return Task.FromResult<ISpeechStream>(new NoSpeech());
        }
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
            Task.FromResult(new Grounding("no_matches", []));
        public async IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            await Task.Yield();
            yield return "A reply.";
        }
        private sealed class NoSpeech : ISpeechStream
        {
            public void Write(byte[] audio) { }
            public Task CompleteInputAsync(CancellationToken cancellation) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
