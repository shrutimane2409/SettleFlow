using SettleFlow.Infrastructure;
using SettleFlow.Processing;

var builder = WebApplication.CreateBuilder(args);
builder.AddSettleFlowDefaults("notification-worker");
builder.AddNotificationProcessing();

var app = builder.Build();
app.MapSettleFlowDefaults();   // /health, /health/ready, /metrics

await app.InitializeSettleFlowAsync();
await app.RunAsync();
