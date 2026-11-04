using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace VoiceAssistant.Api;

/// <summary>
/// One meeting session's protocol v1 socket (contracts/websocket-v1.md): JSON events out, audio and commands in, within the
/// protocol's size, rate and idle limits. Only the session's event loop sends, so sends never overlap.
/// </summary>
internal sealed class SessionWire(WebSocket socket, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads audio and commands until the client closes or stops, writing them to the session's event loop. Audio is held to
    /// real-time speed, commands to 10 per second, and a socket that sends nothing for <paramref name="idleTimeout"/> ends the
    /// session; a protocol violation is written as a fatal event.
    /// </summary>
    public async Task ReceiveLoopAsync(ChannelWriter<SessionEvent> events, TimeSpan idleTimeout, CancellationToken cancellation)
    {
        try
        {
            var audioBudget = 128000d;
            var previous = System.Diagnostics.Stopwatch.GetTimestamp();
            var commands = 0;
            var window = DateTime.UtcNow;
            while (!cancellation.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                idle.CancelAfter(idleTimeout);
                var message = await ReceiveAsync(idle.Token);
                if (message is null) { await events.WriteAsync(new(SessionEventKind.Closed), cancellation); return; }
                if (message.Type == WebSocketMessageType.Binary)
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    audioBudget = Math.Min(128000, audioBudget + System.Diagnostics.Stopwatch.GetElapsedTime(previous, now).TotalSeconds * 64000);
                    previous = now;
                    audioBudget -= message.Bytes.Length;
                    if (message.Bytes.Length == 0 || message.Bytes.Length % 2 != 0 || audioBudget < 0)
                        throw new ProviderException("invalid_audio", "Audio must be bounded, even-length PCM16LE at real-time speed.");
                    await events.WriteAsync(new(SessionEventKind.Audio, new AudioMessage(message.Bytes)), cancellation);
                }
                else
                {
                    if (DateTime.UtcNow - window > TimeSpan.FromSeconds(1)) { window = DateTime.UtcNow; commands = 0; }
                    if (++commands > 10) throw new ProviderException("command_limit", "Too many commands.");
                    var command = ParseCommand(message.Bytes);
                    await events.WriteAsync(new(command.Kind, command.Update), cancellation);
                    if (command.Kind == SessionEventKind.SessionStop) return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException) { await events.WriteAsync(new(SessionEventKind.Fatal, new ProviderException("idle_timeout", "Session idle timeout.")), cancellation); }
        catch (ProviderException exception) { await events.WriteAsync(new(SessionEventKind.Fatal, exception), cancellation); }
        catch (WebSocketException) { await events.WriteAsync(new(SessionEventKind.Closed), cancellation); }
    }

    /// <summary>Whether the first message is a valid protocol v1 <c>session.start</c>, options included.</summary>
    public static bool IsValidStart(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("options", out var options))
                SessionOptions.Parse(options);
            return root.ValueKind == JsonValueKind.Object && root.GetProperty("type").GetString() == "session.start" &&
                root.GetProperty("protocolVersion").GetInt32() == 1 &&
                root.GetProperty("audio").GetProperty("encoding").GetString() == "pcm_s16le" &&
                root.GetProperty("audio").GetProperty("sampleRate").GetInt32() == 16000 &&
                root.GetProperty("audio").GetProperty("channels").GetInt32() == 1;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return false; }
    }

    internal static Command ParseCommand(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var kind = json.RootElement.GetProperty("type").GetString() switch
            {
                "response.request" => SessionEventKind.ResponseRequest,
                "response.cancel" => SessionEventKind.ResponseCancel,
                "session.stop" => SessionEventKind.SessionStop,
                "session.update" => SessionEventKind.SessionUpdate,
                _ => throw new ProviderException("invalid_command", "Unsupported command.")
            };
            return kind == SessionEventKind.SessionUpdate
                ? new(kind, SessionOptions.ParseUpdate(json.RootElement.GetProperty("options")))
                : new(kind);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ProviderException("invalid_command", "Command must be a JSON object with a supported type and valid options."); }
    }

    public async Task<WireMessage?> ReceiveAsync(CancellationToken cancellation, int textLimit = 4096)
    {
        var buffer = new byte[8192];
        using var data = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            var limit = result.MessageType == WebSocketMessageType.Text ? textLimit : 32768;
            if (data.Length + result.Count > limit) throw new ProviderException("message_limit", "Message exceeds size limit.");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return new(data.ToArray(), result.MessageType);
    }

    public async Task SendAsync(object value, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text, true, timeout.Token);
    }

    public Task ErrorAsync(string code, string message, bool retryable, CancellationToken cancellation)
    {
        MeetingMetrics.ErrorSent(code);
        return SendAsync(new { type = "error", code, message, retryable }, cancellation);
    }

    /// <summary>Best-effort error before the session ends: skipped on a closed socket, never throws for a gone client.</summary>
    public async Task TryErrorAsync(string code, string message, bool retryable = true)
    {
        if (socket.State != WebSocketState.Open) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await ErrorAsync(code, message, retryable, timeout.Token); }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        { logger.LogInformation("Meeting error could not be delivered to disconnected client."); }
    }
}
