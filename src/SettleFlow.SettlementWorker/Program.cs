using System.Text.Json;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
var cs = builder.Configuration.GetConnectionString("Postgres") ?? "Host=postgres;Port=5432;Database=settleflow;Username=settleflow;Password=settleflow";
builder.Services.AddDbContext<SettleFlowDbContext>(o=>o.UseNpgsql(cs)); builder.Services.Configure<KafkaConsumerOptions>(builder.Configuration.GetSection("Kafka")); builder.Services.AddHostedService<SettlementWorker>(); builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
await builder.Build().RunAsync();

sealed class SettlementWorker(IOptions<KafkaConsumerOptions> o, IServiceScopeFactory s, ILogger<SettlementWorker> l) : PaymentEventWorker<FraudCompleted>(o,s,l,Topics.FraudCompleted)
{
 protected override Task HandleAsync(EventEnvelope<FraudCompleted> e, SettleFlowDbContext db, CancellationToken ct)
 {
  if(e.Data.Decision!="APPROVED") return Task.CompletedTask;
  var p=db.Payments.Single(x=>x.Id==e.Data.PaymentId); p.Status=PaymentStatus.Settled; p.SettlementReference=$"SET-{p.Id.ToString("N")[..12].ToUpperInvariant()}"; p.UpdatedAt=DateTimeOffset.UtcNow;
  var now=DateTimeOffset.UtcNow; var evt=new EventEnvelope<SettlementCompleted>(Guid.NewGuid().ToString(),nameof(SettlementCompleted),1,now,new SettlementCompleted(p.Id,true,p.SettlementReference,now));
  db.Outbox.Add(new OutboxMessage{Id=Guid.NewGuid(),Topic=Topics.SettlementCompleted,Key=p.Id.ToString(),Payload=JsonSerializer.Serialize(evt),OccurredAt=now}); return Task.CompletedTask;
 }
}
