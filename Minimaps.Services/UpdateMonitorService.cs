using Minimaps.Shared;

namespace Minimaps.Services;

/// <summary>
/// Monitor for the publishing of new WoW products (wow, wowt, wow_beta, etc) and trigger processing when a new build is detected.
/// Builds are treated unique per build version + product name, I don't expect that map changes happen when switching a build from PTR to main without
/// a build change, but I could be wrong, so this is the safest approach...
/// The backend will be queried for unregistered (or registered but unregistered) builds, and begin processing the tile data into the backend tile store.
/// Once all tiles have been published, the build is marked as processed (given it passes a sanity check of everything expected existing in the store etc).
/// </summary>
internal class UpdateMonitorService : IntervalBackgroundService
{
    private readonly Random _random = new();

    public UpdateMonitorService(ILogger<UpdateMonitorService> logger, WebhookEventLog eventLog)
        : base(logger, TimeSpan.FromSeconds(10), eventLog)
    {
    }

    protected override async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_random.Next(1, 2) == 1)
            throw new InvalidOperationException("test failure");
        await Task.Delay(_random.Next(1000, 3000), cancellationToken);
    }
}