using System.Net;
using FluentAssertions;
using SettleFlow.Contracts;
using SettleFlow.Processing;

namespace SettleFlow.Tests;

public class WebhookTests
{
    private const string Secret = "whsec_test";
    private const string Body = """{"type":"payment.settled"}""";

    [Fact]
    public void Signature_round_trips()
    {
        var now = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Compute(Secret, now.ToUnixTimeSeconds(), Body);

        header.Should().StartWith($"t={now.ToUnixTimeSeconds()},v1=");
        WebhookSignature.Verify(Secret, header, Body, TimeSpan.FromMinutes(5), now).Should().BeTrue();
    }

    [Fact]
    public void Tampered_body_wrong_secret_or_old_timestamp_fail()
    {
        var now = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Compute(Secret, now.ToUnixTimeSeconds(), Body);

        WebhookSignature.Verify(Secret, header, Body + " ", TimeSpan.FromMinutes(5), now).Should().BeFalse();
        WebhookSignature.Verify("whsec_other", header, Body, TimeSpan.FromMinutes(5), now).Should().BeFalse();
        WebhookSignature.Verify(Secret, header, Body, TimeSpan.FromMinutes(5), now.AddMinutes(10)).Should().BeFalse();
        WebhookSignature.Verify(Secret, "garbage", Body, TimeSpan.FromMinutes(5), now).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://localhost:8080/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://172.20.1.1/hook")]
    [InlineData("http://192.168.1.1/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("https://user:pass@example.com/hook")]
    [InlineData("not a url")]
    public void Dangerous_urls_are_rejected(string url) =>
        WebhookUrlGuard.TryValidate(url, allowPrivate: false, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("https://hooks.example.com/settleflow")]
    [InlineData("http://203.0.113.10/hook")]
    public void Public_urls_are_accepted(string url) =>
        WebhookUrlGuard.TryValidate(url, allowPrivate: false, out _, out _).Should().BeTrue();

    [Fact]
    public void Private_urls_are_allowed_only_when_enabled() =>
        WebhookUrlGuard.TryValidate("http://webhook-receiver:8080/x", allowPrivate: true, out _, out _).Should().BeTrue();

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::ffff:192.168.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    public void Private_ip_detection(string ip, bool expected) =>
        WebhookUrlGuard.IsPrivate(IPAddress.Parse(ip)).Should().Be(expected);

    [Fact]
    public void Ops_only_notifications_are_not_sent_to_merchants()
    {
        WebhookEvents.FromNotification(NotificationTypes.ReviewRequired).Should().BeNull();
        WebhookEvents.FromNotification(NotificationTypes.Settled).Should().Be("payment.settled");
        WebhookEvents.FromNotification(NotificationTypes.Refunded).Should().Be("refund.succeeded");
    }

    [Fact]
    public void Generated_secrets_are_unique_and_prefixed()
    {
        var a = WebhookSecrets.Generate();
        a.Should().StartWith("whsec_").And.HaveLength(49);
        WebhookSecrets.Generate().Should().NotBe(a);
    }
}
