using FluentAssertions;
using PresenterAi.Application.Auth;
using PresenterAi.Infrastructure.Redis;
using PresenterAi.Integration.Tests.Support;
using StackExchange.Redis;
using Xunit;

namespace PresenterAi.Integration.Tests;

[Collection(IntegrationCollection.Name)]
public sealed class RedisStoreTests(RedisFixture redis)
{
    [Fact]
    public async Task Ticket_issue_claim_and_reuse_are_one_time()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new TicketStore(connection, TimeSpan.FromSeconds(30));
        var ticket = Guid.NewGuid().ToString("N");

        await store.IssueAsync(ticket, "usr_test");

        (await store.ClaimAsync(ticket)).Should().Be("usr_test");
        (await store.ClaimAsync(ticket)).Should().BeNull();
    }

    [Fact]
    public async Task Two_parallel_claims_yield_exactly_one_winner()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new TicketStore(connection, TimeSpan.FromSeconds(30));
        var ticket = Guid.NewGuid().ToString("N");
        await store.IssueAsync(ticket, "usr_parallel");

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => store.ClaimAsync(ticket)));

        claims.Count(claim => claim is not null).Should().Be(1);
        claims.SingleOrDefault(claim => claim is not null).Should().Be("usr_parallel");
    }

    [Fact]
    public async Task Sso_code_claim_is_atomic_and_nonce_replay_is_rejected()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var codeStore = new SsoCodeStore(connection);
        var stateStore = new SsoStateStore(connection);
        var codeHash = Guid.NewGuid().ToString("N");
        var code = new SsoCode("usr_sso", "challenge", "https://example.test/callback");

        await codeStore.IssueAsync(codeHash, code, TimeSpan.FromSeconds(30));

        (await codeStore.ClaimAsync(codeHash)).Should().Be(code);
        (await codeStore.ClaimAsync(codeHash)).Should().BeNull();

        var nonce = Guid.NewGuid().ToString("N");
        (await stateStore.TryConsumeNonceAsync(nonce)).Should().BeTrue();
        (await stateStore.TryConsumeNonceAsync(nonce)).Should().BeFalse();
    }

    [Fact]
    public async Task Ticket_with_a_short_ttl_expires()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new TicketStore(connection, TimeSpan.FromMilliseconds(100));
        var ticket = Guid.NewGuid().ToString("N");

        await store.IssueAsync(ticket, "usr_expiring");
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        (await store.ClaimAsync(ticket)).Should().BeNull();
    }
}
