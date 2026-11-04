using System.Net.WebSockets;
using System.Threading.Channels;

namespace VoiceAssistant.Api;

// The data a meeting session passes between its event loop, its reply generations and its speech recognizers.

internal sealed record RestartedSpeech(ISpeechStream Stream, SpeechAttempt Attempt);

/// <summary>
/// Holds the errors a restarted recognizer reports before the event loop has taken it over, so none is lost and none
/// is mistaken for a failure of a recognizer that is not running yet.
/// </summary>
internal sealed class SpeechAttempt(bool delivered = false)
{
    private readonly object gate = new();
    private bool handedOver = delivered;
    private ProviderException? early;

    public bool HoldIfNotHandedOver(ProviderException failure)
    {
        lock (gate)
        {
            if (handedOver) return false;
            early ??= failure;
            return true;
        }
    }

    public ProviderException? HandOver()
    {
        lock (gate)
        {
            handedOver = true;
            return early;
        }
    }
}
/// <summary>
/// What a session event is. The event loop reads them one at a time: client commands, audio, speech and timer callbacks, and
/// what a reply's generation produced.
/// </summary>
internal enum SessionEventKind
{
    Audio, Transcript, Speculate, Settle, Quiet, Closed, Fatal, SpeechRestarted,
    ResponseRequest, ResponseCancel, SessionUpdate, SessionStop,
    Korean, Delta, Complete, GroundingUnavailable, GroundingFallback, GenerationError
}

internal sealed record SessionEvent(SessionEventKind Kind, object? Value = null, int Generation = 0, long Timestamp = 0, bool ModelResponse = false,
    bool PrimaryDone = false);
internal sealed record AudioMessage(byte[] Bytes);
internal sealed record WireMessage(byte[] Bytes, WebSocketMessageType Type);
internal sealed record Completion(string Text, string[] Suggestions, Source[] Sources, string Grounding, string ResponseRoute, bool RetrievalPrefetched, bool RespondNow = false);
internal sealed record KoreanLine(KoreanScope Scope, int Index, string? Korean);

/// <summary>A reply queued behind the active one: the history it answers, its turn, and the final it is timed from.</summary>
internal sealed record PendingReply(ConversationTurn[] History, string? Turn, long FinalTimestamp);

/// <summary>A reply generated ahead of its final (Session:SpeculativeStart). Nothing of it is sent, logged or measured until the final adopts it.</summary>
internal sealed record Speculation(string TurnId, string Identity, ConversationTurn[] History, string Route, SessionOptions Settings,
    string ResponseId, CancellationTokenSource Cancellation, ReplyOutput Output, MeetingMetrics.ResponseMeasurement Measurement,
    KoreanScope? Scope, Task<RetrievalOutcome>? Prefetched, long StartedAt)
{
    public Task Task { get; set; } = Task.CompletedTask;
}

/// <summary>
/// What one reply's generation produces for the outside: events for the client (through the event loop) and failure log
/// lines. A reply started at its final writes them straight through. A speculative reply holds them until its final adopts
/// it, then hands them over in order as if they were produced just then; a discarded one drops them, so nothing of it is
/// ever sent or logged.
/// </summary>
internal sealed class ReplyOutput
{
    private readonly ChannelWriter<SessionEvent> writer;
    private readonly object gate = new();
    private List<SessionEvent>? heldEvents;
    private List<Action>? heldLogs;
    private bool discarded;
    private int generation;

    /// <summary>Straight through, as the events of reply <paramref name="number"/>.</summary>
    public ReplyOutput(ChannelWriter<SessionEvent> writer, int number)
    {
        this.writer = writer;
        generation = number;
    }

    private ReplyOutput(ChannelWriter<SessionEvent> writer)
    {
        this.writer = writer;
        heldEvents = [];
        heldLogs = [];
    }

    /// <summary>For a speculative reply: everything is held until <see cref="Adopt"/>, or dropped by <see cref="Discard"/>.</summary>
    public static ReplyOutput Held(ChannelWriter<SessionEvent> writer) => new(writer);

    /// <summary>The reply's generation number; a held log line reads it when written, so after adoption it is the adopted one.</summary>
    public int Generation
    {
        get { lock (gate) return generation; }
    }

    public ValueTask WriteAsync(SessionEvent message, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (discarded) return ValueTask.CompletedTask;
            if (heldEvents is not null)
            {
                heldEvents.Add(message);
                return ValueTask.CompletedTask;
            }
            message = message with { Generation = generation };
        }
        return writer.WriteAsync(message, cancellation);
    }

    /// <summary>A failure log line of this reply: written now, or only once a speculative reply is adopted.</summary>
    public void Log(Action write)
    {
        lock (gate)
        {
            if (discarded) return;
            if (heldLogs is not null)
            {
                heldLogs.Add(write);
                return;
            }
        }
        write();
    }

    /// <summary>
    /// The speculative reply became reply <paramref name="number"/>: its held log lines are written, and its held events
    /// are returned in order for the event loop to send now. Later events go straight through.
    /// </summary>
    public SessionEvent[] Adopt(int number)
    {
        List<SessionEvent> events;
        List<Action> logs;
        lock (gate)
        {
            generation = number;
            events = heldEvents ?? [];
            logs = heldLogs ?? [];
            heldEvents = null;
            heldLogs = null;
        }
        foreach (var write in logs) write();
        return events.Select(message => message with { Generation = number }).ToArray();
    }

    public void Discard()
    {
        lock (gate)
        {
            discarded = true;
            heldEvents = null;
            heldLogs = null;
        }
    }
}

/// <summary>Tracks the Korean lines of one response; a response cancelled before it completed never publishes any.</summary>
internal sealed class KoreanScope(string responseId, string? turnId, ReplyOutput output, MeetingMetrics.ResponseMeasurement? measurement = null)
{
    private readonly string?[] lines = new string?[2];
    public string ResponseId { get; } = responseId;
    public string? TurnId { get; } = turnId;
    /// <summary>Where the translated lines go: with the reply's other events, so a speculative reply holds them too.</summary>
    public ReplyOutput Output { get; } = output;
    public MeetingMetrics.ResponseMeasurement? Measurement { get; } = measurement;
    public volatile bool Abandoned;
    /// <summary>False when this exact text is already being translated for the line.</summary>
    public bool Begin(int index, string english) { lock (lines) { if (lines[index] == english) return false; lines[index] = english; return true; } }
    public bool IsCurrent(int index, string english) { lock (lines) return lines[index] == english; }
}

/// <summary>One step of a session's teardown: how long it took from its start (real time), or has taken so far while it runs.</summary>
internal sealed class TeardownStep
{
    private readonly long started;
    private long finished;

    public TeardownStep(Task task, long started)
    {
        this.started = started;
        Task = task;
        _ = task.ContinueWith(_ => Interlocked.Exchange(ref finished, System.Diagnostics.Stopwatch.GetTimestamp()), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task Task { get; }

    public long Milliseconds => (long)System.Diagnostics.Stopwatch.GetElapsedTime(started,
        Interlocked.Read(ref finished) is var end and not 0 ? end : System.Diagnostics.Stopwatch.GetTimestamp()).TotalMilliseconds;
}

internal sealed record Command(SessionEventKind Kind, SessionUpdate? Update = null);
