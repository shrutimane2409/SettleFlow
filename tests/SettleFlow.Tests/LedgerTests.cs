using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Infrastructure;

namespace SettleFlow.Tests;

public class LedgerTests
{
    [Theory]
    [InlineData(100, 150, 0.25, 1.75)]     // 1.5% + 0.25
    [InlineData(33.33, 150, 0.25, 0.75)]   // 0.49995 + 0.25 = 0.74995 → 0.75 (half away from zero)
    [InlineData(0.10, 150, 0.25, 0.10)]    // fee can never exceed the amount
    [InlineData(1000, 0, 0, 0)]
    [InlineData(250, 290, 0.30, 7.55)]     // "2.9% + 30c"
    public void Fee_is_rounded_and_capped(decimal amount, int bps, decimal fixedFee, decimal expected) =>
        LedgerExtensions.ComputeFee(amount, bps, fixedFee).Should().Be(expected);

    private static SettleFlowDbContext Db() =>
        new(new DbContextOptionsBuilder<SettleFlowDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Unbalanced_transactions_are_rejected_before_touching_the_database()
    {
        var act = () => Db().PostLedgerAsync("test", Guid.NewGuid(), "EUR", "x", CancellationToken.None,
            ("acquirer:clearing:EUR", 100m), ("merchant:m:balance:EUR", -99m));
        (await act.Should().ThrowAsync<UnbalancedLedgerTransactionException>()).WithMessage("*does not balance*");
    }

    [Fact]
    public async Task Zero_or_fractional_cent_amounts_are_rejected()
    {
        await FluentActions.Awaiting(() => Db().PostLedgerAsync("test", Guid.NewGuid(), "EUR", "x", CancellationToken.None,
            ("a", 0m), ("b", 0m))).Should().ThrowAsync<UnbalancedLedgerTransactionException>();
        await FluentActions.Awaiting(() => Db().PostLedgerAsync("test", Guid.NewGuid(), "EUR", "x", CancellationToken.None,
            ("a", 0.005m), ("b", -0.005m))).Should().ThrowAsync<UnbalancedLedgerTransactionException>();
    }

    [Fact]
    public void Account_codes_describe_themselves()
    {
        LedgerAccounts.Describe(LedgerAccounts.MerchantBalance("acme", "EUR")).Should().Be((LedgerAccounts.KindMerchantBalance, "acme"));
        LedgerAccounts.Describe(LedgerAccounts.Fees("EUR")).Should().Be((LedgerAccounts.KindFees, "platform"));
        LedgerAccounts.Describe(LedgerAccounts.Clearing("GBP")).Kind.Should().Be(LedgerAccounts.KindClearing);
    }
}
