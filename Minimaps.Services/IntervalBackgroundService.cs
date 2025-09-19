using Minimaps.Shared;

namespace Minimaps.Services;

/// <summary>
/// Background service ticking at fixed interval
/// </summary>
internal abstract class IntervalBackgroundService(ILogger logger, TimeSpan interval, WebhookEventLog eventLog) : BackgroundService
{
    protected readonly ILogger Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeSpan _interval = interval;
    private readonly WebhookEventLog _eventLog = eventLog;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Starting {ServiceName} @ {Interval}", GetType().Name, _interval);
        _eventLog.Post($"Started {GetType().Name} @ {_interval}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Logger.LogTrace("Starting tick");
                await TickAsync(stoppingToken);
                Logger.LogTrace("Finished tick in {ElapsedMilliseconds}ms", timer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                break; // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in {ServiceName}: {Msg}", GetType().Name, ex.Message);
                _eventLog.Post($"Error in {GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Logger.LogInformation("Stopped {ServiceName}", GetType().Name);
        _eventLog.Post($"Stopped {GetType().Name}");
    }

    protected abstract Task TickAsync(CancellationToken cancellationToken);
}