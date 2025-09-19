using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minimaps.CLI.Services;
using Minimaps.Shared;
using System.CommandLine;

namespace Minimaps.CLI.Commands;

public static class ServiceCommand
{
    public static Command Create(IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var tickInterval = TimeSpan.FromSeconds(configuration.GetValue("Services:TickIntervalSeconds", 1.0f));
        var command = new Command("service", "Service management");

        command.SetAction(async parseResult =>
        {
            var logger = loggerFactory.CreateLogger("ServiceWorker");
            logger.LogInformation("Service starting");

            var webhookUrl = configuration.GetValue<string>("Services:EventWebhook");
            using var eventLog = new WebhookEventLog(webhookUrl, logger);

            var services = new List<BaseService>()
            {
                new UpdateMonitorService()
            };

            eventLog.Post("Service started");
            logger.LogInformation("ServiceWorker configured with {ServiceCount} services: {ServiceNames}", services.Count, string.Join(',', services.Select(x => x.Name)));

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var servicesToRun = services.Where(s => s.IsDue).ToList();
                    if (servicesToRun.Count != 0)
                    {
                        logger.LogDebug("Running {ServiceCount} due services", servicesToRun.Count);
                        
                        try
                        {
                            await Task.WhenAll(servicesToRun.Select(s => WrappedServiceTick(s, logger, cancellationToken, eventLog)));
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error during service tick");
                            eventLog.Post($"Service tick error: {ex.Message}");
                        }
                    }

                    // Process event log messages & sleep until next check
                    await Task.WhenAll(
                        Task.Delay(tickInterval, cancellationToken),
                        eventLog.SendQueuedAsync() ?? Task.CompletedTask
                    );
                }
            }
            catch (OperationCanceledException)
            {
                eventLog.Post($"Services aborted");
            }
            catch (Exception ex)
            {
                eventLog.Post($"Unhandled service error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                eventLog.Post("Service stopping");
                await eventLog.SendQueuedAsync();
                logger.LogInformation("Service stopped");
            }
        });

        return command;
    }

    private static async Task WrappedServiceTick(BaseService service, ILogger logger, CancellationToken cancellationToken, WebhookEventLog eventLog)
    {
        using var scope = logger.BeginScope($"[Service:{service.Name}]");

        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            logger.LogDebug("Starting");
            await Task.Run(() => service.TickAsync(cancellationToken), cancellationToken);
            logger.LogDebug("Finished in {ms}ms", timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw; // bubble up
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in service {ServiceName}", service.Name);
            eventLog.Post($"Service {service.Name} error: {ex.Message}\n{ex.StackTrace}", false);
        }
    }
}