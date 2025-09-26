using Minimaps.Shared;

namespace Minimaps.Services;

/// <summary>
/// Hosted service that manages webhook event logging for all background services
/// </summary>
internal class EventLoggerService(ILogger<EventLoggerService> logger, WebhookEventLog eventLog) : BackgroundService
{
    private readonly ILogger<EventLoggerService> _logger = logger;
    private readonly WebhookEventLog _eventLog = eventLog;
    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(2);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _eventLog.Post("Background services started");
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _eventLog.Post("Background services stopping");
        await _eventLog.SendQueuedAsync();
        _eventLog.Dispose();
        _logger.LogInformation("Service event logger stopped");
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_eventLog == null)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushInterval, stoppingToken);
                await _eventLog.SendQueuedAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending queued webhook events");
            }
        }
    }
}