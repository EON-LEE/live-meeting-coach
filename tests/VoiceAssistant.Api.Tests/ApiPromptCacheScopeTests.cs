using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

// Azure:ChatPromptCacheKey=session keys the answer with the session's cache scope: random per session, never the user.
public sealed partial class ApiTests
{
    [Fact]
    public async Task EachSessionHasItsOwnRandomCacheScopeThatAnUpdateKeeps()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider);
        var scopes = new List<string>();
        for (var session = 0; session < 2; session++)
        {
            using var socket = await host.ConnectAsync();
            await Send(socket, WithOptions(new { responseMode = "conversation" }));
            Assert.Equal("session.ready", TypeOf(await Receive(socket)));
            provider.Emit(new("one", 1, "Which plan should we pick?", true));
            await ReceiveUntilAsync(socket, Completed);
            var scope = provider.AnswerOptions!.CacheScope;
            Assert.Matches("^[0-9a-f]{32}$", scope);
            Assert.DoesNotContain(ObjectId.Replace("-", ""), scope);

            await Send(socket, UpdateCommand(new { responseMode = "grounded" }));
            await Until(socket, "session.updated");
            provider.Emit(new("two", 1, "Can we commit to a date?", true));
            await ReceiveUntilAsync(socket, Completed);
            Assert.Equal("grounded", provider.AnswerOptions.ResponseMode);
            Assert.Equal(scope, provider.AnswerOptions.CacheScope);
            scopes.Add(scope);
            await Send(socket, """{"type":"session.stop"}""");
            await ReceiveUntilCloseAsync(socket);
        }
        Assert.NotEqual(scopes[0], scopes[1]);
    }
}
