namespace VoiceAssistant.Api;

public sealed class SessionSlots
{
    private readonly Dictionary<string, Slot> active = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a reconnect waits (real time) for the superseded session of the same user to release its slot before it is
    /// refused with HTTP 429. That session may first spend up to 2 s telling its client it was superseded, then at most
    /// <see cref="MeetingSession.ShutdownBudget"/> tearing down, so even its worst case ends well inside this wait. The live page
    /// allows 15 s for a connection.
    /// </summary>
    internal static readonly TimeSpan SupersedeWait = TimeSpan.FromSeconds(8);

    /// <summary>Acquires the identity's single meeting slot, superseding a stale session of the same identity.</summary>
    public async Task<SessionLease?> AcquireAsync(string objectId, CancellationToken cancellation)
    {
        Slot? previous;
        lock (active)
        {
            if (!active.TryGetValue(objectId, out previous) && active.Count >= 100) return null;
            if (previous is null) return Register(objectId);
        }
        // A reconnect after a network drop can arrive before the old socket notices the drop. The old session's teardown is
        // bounded (MeetingSession.ShutdownBudget), so it releases the slot well within this wait even when its cleanup hangs.
        previous.Supersede.Cancel();
        try { await previous.Released.Task.WaitAsync(SupersedeWait, cancellation); }
        catch (TimeoutException) { return null; }
        lock (active)
            return active.ContainsKey(objectId) ? null : Register(objectId);
    }

    private SessionLease Register(string objectId)
    {
        var slot = new Slot();
        active.Add(objectId, slot);
        return new SessionLease(slot.Supersede.Token, () =>
        {
            lock (active) if (active.TryGetValue(objectId, out var current) && current == slot) active.Remove(objectId);
            slot.Released.TrySetResult();
            slot.Supersede.Dispose();
        });
    }

    private sealed class Slot
    {
        public CancellationTokenSource Supersede { get; } = new();
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class SessionLease(CancellationToken superseded, Action release) : IDisposable
{
    private Action? release = release;
    public CancellationToken Superseded { get; } = superseded;
    public void Dispose() => Interlocked.Exchange(ref this.release, null)?.Invoke();
}
