using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SettleFlow.Infrastructure;

namespace SettleFlow.Tests;

public class HealthTests
{
    [Fact]
    public async Task Draining_check_turns_unhealthy_on_shutdown()
    {
        var check = new DrainingHealthCheck();
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        check.Drain();
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
    }
}
