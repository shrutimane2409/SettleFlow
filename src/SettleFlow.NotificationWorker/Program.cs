using System.Text.Json;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder=Host.CreateApplicationBuilder(args); var cs=builder.Configuration.GetConnectionString("Postgres")??"Host=postgres;Port=5432;Database=settleflow;Username=settleflow;Password=settleflow"; builder.Services.AddDbContext<SettleFlowDbContext>(o=>o.UseNpgsql(cs)); builder.Services.Configure<KafkaConsumerOptions>(builder.Configuration.GetSection("Kafka")); builder.Services.AddHostedService<SettlementNotificationWorker>(); builder.Services.AddHostedService<FailureNotificationWorker>(); builder.Services.AddHostedService<NotificationSinkWorker>(); await builder.Build().RunAsync();

sealed class SettlementNotificationWorker(IOptions<KafkaConsumerOptions> o,IServiceScopeFactory s,ILogger<SettlementNotificationWorker> l):PaymentEventWorker<SettlementCompleted>(o,s,l,Topics.SettlementCompleted)
{ protected override Task HandleAsync(EventEnvelope<SettlementCompleted> e,SettleFlowDbContext db,CancellationToken ct){var now=DateTimeOffset.UtcNow;db.Outbox.Add(new OutboxMessage{Id=Guid.NewGuid(),Topic=Topics.NotificationsRequested,Key=e.Data.PaymentId.ToString(),Payload=JsonSerializer.Serialize(new EventEnvelope<NotificationRequested>(Guid.NewGuid().ToString(),nameof(NotificationRequested),1,now,new NotificationRequested(e.Data.PaymentId,"SETTLED",$"Payment settled: {e.Data.Reference}",now))),OccurredAt=now});return Task.CompletedTask;} }
sealed class FailureNotificationWorker(IOptions<KafkaConsumerOptions> o,IServiceScopeFactory s,ILogger<FailureNotificationWorker> l):PaymentEventWorker<PaymentFailed>(o,s,l,Topics.PaymentFailed)
{ protected override Task HandleAsync(EventEnvelope<PaymentFailed> e,SettleFlowDbContext db,CancellationToken ct){var now=DateTimeOffset.UtcNow;db.Outbox.Add(new OutboxMessage{Id=Guid.NewGuid(),Topic=Topics.NotificationsRequested,Key=e.Data.PaymentId.ToString(),Payload=JsonSerializer.Serialize(new EventEnvelope<NotificationRequested>(Guid.NewGuid().ToString(),nameof(NotificationRequested),1,now,new NotificationRequested(e.Data.PaymentId,"FAILED",$"Payment failed at {e.Data.Stage}: {e.Data.Reason}",now))),OccurredAt=now});return Task.CompletedTask;} }

sealed class NotificationSinkWorker(IOptions<KafkaConsumerOptions> o,IServiceScopeFactory s,ILogger<NotificationSinkWorker> l):PaymentEventWorker<NotificationRequested>(o,s,l,Topics.NotificationsRequested)
{ protected override Task HandleAsync(EventEnvelope<NotificationRequested> e,SettleFlowDbContext db,CancellationToken ct){l.LogInformation("NOTIFICATION | Payment {PaymentId} | Type {Type} | {Message}",e.Data.PaymentId,e.Data.Type,e.Data.Message);return Task.CompletedTask;} }
