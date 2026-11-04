using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SettleFlow.Infrastructure;
using SettleFlow.Processing;

namespace SettleFlow.Tests;

public class SettlementGatewayTests
{
    private static IServiceScopeFactory InMemoryDb()
    {
        var name = Guid.NewGuid().ToString();
        return new ServiceCollection()
            .AddDbContext<SettleFlowDbContext>(o => o.UseInMemoryDatabase(name))
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private static SimulatedSettlementGateway Gateway(double failureRate = 0, IServiceScopeFactory? db = null, int slowMs = 5000) =>
        new(Options.Create(new SettlementOptions { TransientFailureRate = failureRate, MinLatencyMs = 0, MaxLatencyMs = 0, SlowLatencyMs = slowMs }),
            db ?? InMemoryDb());

    private static readonly Guid PaymentId = Guid.Parse("8fa2881c-bd8c-47a3-aa90-74f404ec8f6d");

    private static SettlementRequest Request(string merchant = "merchant_demo", string currency = "EUR") =>
        new(PaymentId, 100m, currency, merchant);

    [Fact]
    public async Task Settles_with_deterministic_reference()
    {
        var outcome = await Gateway().SettleAsync(Request(), CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Reference.Should().Be("SET-8FA2881CBD8C");
    }

    [Fact]
    public async Task Blocked_merchant_is_declined()
    {
        var outcome = await Gateway().SettleAsync(Request(merchant: "blocked_shop"), CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.DeclineReason.Should().Be("merchant_blocked");
    }

    [Fact]
    public async Task Unsupported_currency_is_declined() =>
        (await Gateway().SettleAsync(Request(currency: "JPY"), CancellationToken.None)).DeclineReason.Should().Be("unsupported_currency");

    [Fact]
    public async Task Outage_merchant_throws_retryable_error()
    {
        var act = () => Gateway().SettleAsync(Request(merchant: "merchant_outage"), CancellationToken.None);
        await act.Should().ThrowAsync<SettlementGatewayUnavailableException>();
    }

    [Fact]
    public async Task Repeated_calls_are_idempotent()
    {
        var db = InMemoryDb();
        var first = await Gateway(db: db).SettleAsync(Request(), CancellationToken.None);

        // Even with the acquirer now failing every call, the recorded result is returned: no double charge.
        var second = await Gateway(failureRate: 1, db: db).SettleAsync(Request(), CancellationToken.None);

        second.Should().Be(first);
        using var scope = db.CreateScope();
        scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>().AcquirerTransactions.Should().ContainSingle();
    }

    [Fact]
    public async Task Refunds_are_recorded_separately_from_captures()
    {
        var db = InMemoryDb();
        var gw = Gateway(db: db);
        await gw.SettleAsync(Request(), CancellationToken.None);
        var refund = await gw.RefundAsync(new RefundGatewayRequest(Guid.NewGuid(), PaymentId, 40m, "EUR", "merchant_demo"), CancellationToken.None);

        refund.Success.Should().BeTrue();
        refund.Reference.Should().StartWith("RFD-");
        using var scope = db.CreateScope();
        scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>().AcquirerTransactions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Slow_acquirer_hits_the_timeout()
    {
        var resilient = new ResilientSettlementGateway(Gateway(slowMs: 2000),
            Options.Create(new SettlementOptions { Timeout = TimeSpan.FromMilliseconds(100) }), NullLogger<ResilientSettlementGateway>.Instance);

        var act = () => resilient.SettleAsync(Request(merchant: "merchant_slow"), CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures_and_isolates_the_merchant()
    {
        var resilient = new ResilientSettlementGateway(Gateway(),
            Options.Create(new SettlementOptions { CircuitMinimumThroughput = 2, CircuitFailureRatio = 0.5, CircuitBreakDuration = TimeSpan.FromSeconds(30) }),
            NullLogger<ResilientSettlementGateway>.Instance);

        for (var i = 0; i < 3; i++)
            await FluentActions.Awaiting(() => resilient.SettleAsync(Request(merchant: "merchant_outage"), CancellationToken.None))
                .Should().ThrowAsync<Exception>();

        // Now it fails fast without calling the acquirer...
        await FluentActions.Awaiting(() => resilient.SettleAsync(Request(merchant: "merchant_outage"), CancellationToken.None))
            .Should().ThrowAsync<BrokenCircuitException>();
        ResilientSettlementGateway.IsOpen("merchant_outage").Should().BeTrue();

        // ...while other merchants keep working.
        (await resilient.SettleAsync(Request(), CancellationToken.None)).Success.Should().BeTrue();
    }
}
