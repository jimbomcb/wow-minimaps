namespace Minimaps.Services.Blizztrack;

internal class BlizztrackConfig
{
    public string CachePath { get; set; } = string.Empty;
    public int RateLimitPermits { get; set; } = 10;
    public int RateLimitWindowSeconds { get; set; } = 60;
    public int RateLimitSegments { get; set; } = 12;
    public int QueueLimit { get; set; } = int.MaxValue;
    public int ConcurrencyLimit { get; set; } = 3;
    public int ConcurrencyQueueLimit { get; set; } = int.MaxValue;
    public int MaxRetryAttempts { get; set; } = 3;
    public double RetryBaseDelaySeconds { get; set; } = 1.0;
    public double RetryMaxDelaySeconds { get; set; } = 30.0;
}
