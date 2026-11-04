using FluentAssertions;
using Microsoft.Extensions.Options;
using SettleFlow.Processing;

namespace SettleFlow.Tests;

public class SettlementGatewayTests
{
    private static SimulatedSettlementGateway Gateway(double failureRate = 0) =>
        new(Options.Create(new SettlementOptions { TransientFailureRate = failureRate, MinLatencyMs = 0, MaxLatencyMs = 0 }));

    private static SettlementRequest Request(string merchant = "merchant_demo", string currency = "EUR") =>
        new(Guid.Parse("8fa2881c-bd8c-47a3-aa90-74f404ec8f6d"), 100m, currency, merchant);

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
    public async Task Unsupported_currency_is_declined()
    {
        var outcome = await Gateway().SettleAsync(Request(currency: "JPY"), CancellationToken.None);

        outcome.DeclineReason.Should().Be("unsupported_currency");
    }

    [Fact]
    public async Task Outage_merchant_throws_retryable_error()
    {
        var act = () => Gateway().SettleAsync(Request(merchant: "merchant_outage"), CancellationToken.None);

        await act.Should().ThrowAsync<SettlementGatewayUnavailableException>();
    }

    [Fact]
    public async Task Transient_failures_follow_configured_rate()
    {
        var act = () => Gateway(failureRate: 1).SettleAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<SettlementGatewayUnavailableException>();
    }
}
