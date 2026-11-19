using System.Net;
using Agent.IntegrationTests.Support;

namespace Agent.IntegrationTests;

public sealed class RetentionApiTests
{
    [Fact]
    public async Task The_hosted_purge_removes_an_abandoned_session_once_the_retention_horizon_passes()
    {
        using var factory = new ApiFactory(overrides: new Dictionary<string, string?>
        {
            ["Auth:SessionTokenLifetimeMinutes"] = "1440",
            ["Retention:RunIntervalMinutes"] = "15",
        });
        var api = new TestApi(factory.CreateClient());
        var abandoned = await api.StartSessionAsync();
        Assert.Equal(HttpStatusCode.OK, (await api.MessageAsync(abandoned, "hello")).StatusCode);

        // Idle for longer than the 30 minute TTL but inside the grace period: expired (410), data still stored.
        factory.Clock.Advance(TimeSpan.FromMinutes(45));
        Assert.Equal(HttpStatusCode.Gone, (await api.MessageAsync(abandoned, "hello again")).StatusCode);

        // Past TTL plus grace the timer tick deletes it, and the same token now points at nothing.
        factory.Clock.Advance(TimeSpan.FromMinutes(60));
        await WaitUntilAsync(async () => (await api.MessageAsync(abandoned, "anyone?")).StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_session_in_use_is_never_purged()
    {
        using var factory = new ApiFactory(overrides: new Dictionary<string, string?>
        {
            ["Auth:SessionTokenLifetimeMinutes"] = "1440",
            ["Retention:RunIntervalMinutes"] = "15",
        });
        var api = new TestApi(factory.CreateClient());
        var token = await api.StartSessionAsync();

        for (var i = 0; i < 8; i++)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, (await api.MessageAsync(token, $"still here {i}")).StatusCode);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The purge did not remove the session in time.");
            await Task.Delay(50);
        }
    }
}
