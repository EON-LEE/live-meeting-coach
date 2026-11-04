using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class SessionSlotTests
{
    [Fact]
    public async Task ReconnectSupersedesStaleSameIdentitySessionInsteadOfRejecting()
    {
        var slots = new SessionSlots();
        var stale = await slots.AcquireAsync("user-a", CancellationToken.None);
        Assert.NotNull(stale);
        Assert.False(stale.Superseded.IsCancellationRequested);
        var reconnect = slots.AcquireAsync("user-a", CancellationToken.None);
        Assert.True(stale.Superseded.IsCancellationRequested);
        Assert.False(reconnect.IsCompleted);
        stale.Dispose();
        var fresh = await reconnect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(fresh);
        Assert.False(fresh.Superseded.IsCancellationRequested);
        stale.Dispose();
        var third = slots.AcquireAsync("user-a", CancellationToken.None);
        Assert.True(fresh.Superseded.IsCancellationRequested);
        Assert.False(third.IsCompleted);
        fresh.Dispose();
        using var after = await third.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(after);
    }

    [Fact]
    public async Task DifferentIdentitiesNeverSupersedeEachOther()
    {
        var slots = new SessionSlots();
        using var a = await slots.AcquireAsync("user-a", CancellationToken.None);
        using var b = await slots.AcquireAsync("user-b", CancellationToken.None);
        Assert.NotNull(b);
        Assert.False(a!.Superseded.IsCancellationRequested);
    }

    [Fact]
    public async Task AReconnectRacingTheOldSessionsReleaseStillGetsTheSlot()
    {
        // The old session may release its slot (disposing its token source) between the reconnect's lookup and its supersede.
        var slots = new SessionSlots();
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var stale = await slots.AcquireAsync("user-a", CancellationToken.None);
            Assert.NotNull(stale);
            using var start = new Barrier(2);
            var release = Task.Run(() =>
            {
                start.SignalAndWait();
                stale.Dispose();
            });
            var reconnect = Task.Run(async () =>
            {
                start.SignalAndWait();
                return await slots.AcquireAsync("user-a", CancellationToken.None);
            });
            await release;
            using var fresh = await reconnect.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(fresh);
        }
    }
}
