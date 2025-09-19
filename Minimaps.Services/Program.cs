using Minimaps.Services;
using Minimaps.Shared;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var logger = serviceProvider.GetRequiredService<ILogger<WebhookEventLog>>();
    return new WebhookEventLog(configuration.GetValue<string>("Services:EventWebhook"), logger);
});

builder.Services.AddHostedService<ServiceEventLoggerService>();
builder.Services.AddHostedService<UpdateMonitorService>();

var host = builder.Build();
await host.RunAsync();