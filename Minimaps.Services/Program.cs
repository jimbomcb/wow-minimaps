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

builder.Services.AddHostedService<EventLoggerService>();
builder.Services.AddHostedService<UpdateMonitorService>();

var backendUrl = builder.Configuration.GetValue<string>("BackendUrl");
if (string.IsNullOrEmpty(backendUrl))
    throw new Exception("Backend URL is not configured, cannot start service");

builder.Services.AddHttpClient<BackendClient>(client =>
{
    client.BaseAddress = new Uri(backendUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
});

var host = builder.Build();
await host.RunAsync();