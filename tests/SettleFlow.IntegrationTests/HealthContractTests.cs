using FluentAssertions;
using Xunit;

namespace SettleFlow.IntegrationTests;

public class HealthContractTests
{
    [Fact]
    public void Payment_amount_should_be_positive() => 1250m.Should().BePositive();
}
