namespace Minimaps.CLI.Services;

internal abstract class BaseService(string Name, TimeSpan Interval)
{
    public string Name { get; } = Name ?? throw new ArgumentNullException(nameof(Name));
    public TimeSpan Interval { get; } = Interval;
    public DateTime LastRun { get; private set; } = DateTime.MinValue;
    public bool IsDue => DateTime.UtcNow >= LastRun + Interval;
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        LastRun = DateTime.UtcNow;
        await TickInternalAsync(cancellationToken);
    }

    protected abstract Task TickInternalAsync(CancellationToken cancellationToken);
}
