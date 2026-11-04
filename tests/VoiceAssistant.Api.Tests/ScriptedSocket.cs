using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VoiceAssistant.Api.Tests;

/// <summary>
/// An in-memory WebSocket for driving <see cref="MeetingSession"/> directly: the test queues what the client sends and reads
/// what the server sent. Sends can be held to model a client that stops reading, and the session's close is answered like a
/// browser answers it.
/// </summary>
internal sealed class ScriptedSocket : WebSocket
{
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private readonly Channel<JsonElement> sent = Channel.CreateUnbounded<JsonElement>();
    private WebSocketState state = WebSocketState.Open;
    private volatile bool holdSends;
    private readonly TaskCompletionSource sendHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<JsonElement> Sent { get; } = new();

    /// <summary>Completes when a send first starts waiting under <see cref="HoldSends"/>.</summary>
    public Task SendHeld => sendHeld.Task;

    public void Client(string text) => incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));

    /// <summary>From now on a send waits until it is cancelled; the first cancelled send ends the hold.</summary>
    public void HoldSends() => holdSends = true;

    /// <summary>The next message the server sent, bounded by real time.</summary>
    public async Task<JsonElement> NextAsync(TimeSpan? timeout = null) =>
        await sent.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

    public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Open ? null : WebSocketCloseStatus.NormalClosure;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;

    public override void Abort() => state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        state = WebSocketState.CloseSent;
        // The client answers the close frame.
        incoming.Writer.TryWrite(([], WebSocketMessageType.Close));
        return Task.CompletedTask;
    }

    public override void Dispose() => state = WebSocketState.Closed;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var (bytes, type) = await incoming.Reader.ReadAsync(cancellationToken);
        if (type == WebSocketMessageType.Close) return new(0, type, true, WebSocketCloseStatus.NormalClosure, null);
        bytes.CopyTo(buffer.Array!, buffer.Offset);
        return new(bytes.Length, type, true);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        if (holdSends)
        {
            sendHeld.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { holdSends = false; }
        }
        using var json = JsonDocument.Parse(buffer.AsMemory());
        var message = json.RootElement.Clone();
        Sent.Enqueue(message);
        sent.Writer.TryWrite(message);
    }
}
