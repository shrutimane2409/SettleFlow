using System.Text.Json;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
var cs = builder.Configuration.GetConnectionString("Postgres") ?? "Host=postgres;Port=5432;Database=settleflow;Username=settleflow;Password=settleflow";
builder.Services.AddDbContext<SettleFlowDbContext>(o => o.UseNpgsql(cs));
builder.Services.Configure<KafkaConsumerOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.AddHostedService<FraudWorker>();
builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
await builder.Build().RunAsync();

sealed class FraudWorker(IOptions<KafkaConsumerOptions> o, IServiceScopeFactory s, ILogger<FraudWorker> l) : PaymentEventWorker<PaymentCreated>(o,s,l,Topics.PaymentsCreated)
{
    protected override Task HandleAsync(EventEnvelope<PaymentCreated> e, SettleFlowDbContext db, CancellationToken ct)
    {
        var p = db.Payments.Single(x => x.Id == e.Data.PaymentId);
        var reasons = new List<string>(); var score=0;
        if (e.Data.Amount > 5000) { score += 35; reasons.Add("high_amount"); }
        if (!new[]{"IE","DE","FR","NL","ES","GB"}.Contains(e.Data.Country)) { score += 25; reasons.Add("country_risk"); }
        if (e.Data.Amount % 10 == 0) { score += 10; reasons.Add("round_amount"); }
        var decision = score >= 50 ? "DECLINED" : score >= 25 ? "REVIEW" : "APPROVED";
        p.RiskScore=score; p.RiskReasonsJson=JsonSerializer.Serialize(reasons); p.Status=decision switch { "APPROVED"=>PaymentStatus.FraudApproved, "REVIEW"=>PaymentStatus.FraudReview, _=>PaymentStatus.Declined }; p.UpdatedAt=DateTimeOffset.UtcNow;
        var evt = new EventEnvelope<FraudCompleted>(Guid.NewGuid().ToString(), nameof(FraudCompleted),1,DateTimeOffset.UtcNow,new FraudCompleted(p.Id,decision,score,reasons.ToArray(),DateTimeOffset.UtcNow));
        db.Outbox.Add(new OutboxMessage { Id=Guid.NewGuid(), Topic=Topics.FraudCompleted, Key=p.Id.ToString(), Payload=JsonSerializer.Serialize(evt), OccurredAt=DateTimeOffset.UtcNow });
        if (decision != "APPROVED") db.Outbox.Add(new OutboxMessage { Id=Guid.NewGuid(), Topic=Topics.PaymentFailed, Key=p.Id.ToString(), Payload=JsonSerializer.Serialize(new EventEnvelope<PaymentFailed>(Guid.NewGuid().ToString(),nameof(PaymentFailed),1,DateTimeOffset.UtcNow,new PaymentFailed(p.Id,"fraud",string.Join(',',reasons),DateTimeOffset.UtcNow))), OccurredAt=DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }
}
