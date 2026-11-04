using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.Api.Tests;

/// <summary>Keeps every log entry: its level and formatted message, followed by any exception text.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
    public IReadOnlyList<string> Lines => Entries.Select(entry => entry.Message).ToArray();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
}
