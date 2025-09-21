using Blizztrack.Framework.Extensions.Services;
using Blizztrack.Framework.TACT.Resources;
using Polly;
using Polly.Retry;
using Polly.Telemetry;
using System.Threading.RateLimiting;

namespace Minimaps.Services.Blizztrack
{
    /// <summary>
    /// Blizztrack resource locator that just stores on-disk and pulls from the CDN on demand, 
    /// we're not streaming much and should be able to cache efficiently.
    /// </summary>
    internal class ResourceLocService :
        AbstractResourceLocatorService
    {
        private readonly BlizztrackConfig _config = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ResourceLocService> _logger;
        private readonly ResiliencePipeline<ContentQueryResult> _acquisitionPipeline;

        public ResourceLocService(IHttpClientFactory clientFactory, IServiceProvider serviceProvider, IConfiguration config) : base(clientFactory)
        {
            _serviceProvider = serviceProvider;
            _logger = serviceProvider.GetRequiredService<ILogger<ResourceLocService>>();

            config.GetSection("Blizztrack").Bind(_config);

            if (string.IsNullOrEmpty(_config.CachePath))
                throw new ArgumentException("Blizztrack:CachePath must be set in configuration");

            Directory.CreateDirectory(_config.CachePath);

            _acquisitionPipeline = new ResiliencePipelineBuilder<ContentQueryResult>()
                .AddConcurrencyLimiter(_config.ConcurrencyLimit, _config.ConcurrencyQueueLimit)
                .AddRateLimiter(
                    new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions()
                    {
                        PermitLimit = _config.RateLimitPermits,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        Window = TimeSpan.FromSeconds(_config.RateLimitWindowSeconds),
                        SegmentsPerWindow = _config.RateLimitSegments,
                        QueueLimit = _config.QueueLimit,
                        AutoReplenishment = true
                    }))
                .AddRetry(new RetryStrategyOptions<ContentQueryResult>()
                {
                    BackoffType = DelayBackoffType.Constant,
                    MaxRetryAttempts = _config.MaxRetryAttempts,
                    ShouldHandle = static args => args.Outcome switch
                    {
                        { Exception: HttpRequestException } => PredicateResult.True(),
                        { Exception: TaskCanceledException } => PredicateResult.True(),
                        { Result.StatusCode: System.Net.HttpStatusCode.TooManyRequests } => PredicateResult.True(),
                        { Result.StatusCode: System.Net.HttpStatusCode.ServiceUnavailable } => PredicateResult.True(),
                        { Result.StatusCode: System.Net.HttpStatusCode.BadGateway } => PredicateResult.True(),
                        { Result.StatusCode: System.Net.HttpStatusCode.NotFound } => PredicateResult.False(),
                        _ => PredicateResult.False()
                    }
                })
                .ConfigureTelemetry(new TelemetryOptions()
                {
                    LoggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>()
                })
                .Build();
        }

        protected override ResiliencePipeline<ContentQueryResult> AcquisitionPipeline => _acquisitionPipeline;

        public override ResourceHandle CreateLocalHandle(ResourceDescriptor resourceDescriptor, byte[] fileData)
        {
            var localPath = Path.Combine(_config.CachePath, "res", resourceDescriptor.LocalPath);
            var directory = Path.GetDirectoryName(localPath)!;
            Directory.CreateDirectory(directory);

            // avoid writing partial files via temp + rename
            var tempPath = Path.Combine(directory, $"{Path.GetFileName(localPath)}.tmp.{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(tempPath, fileData);
                File.Move(tempPath, localPath, overwrite: true);
                return new(localPath);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to clean up temporary file: {TempPath}", tempPath);
                }

                throw;
            }
        }

        public override ResourceHandle OpenLocalHandle(ResourceDescriptor resourceDescriptor)
        {
            var localPath = Path.Combine(_config.CachePath, "res", resourceDescriptor.LocalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            return new(localPath);
        }

        public override Task<ResourceHandle> OpenCompressedHandle(ResourceDescriptor compressedDescriptor, CancellationToken stoppingToken)
        {
            throw new NotImplementedException();
        }

        public override global::Blizztrack.Framework.TACT.ContentKey ResolveContentKey(in global::Blizztrack.Framework.TACT.Views.EncodingKey encodingKey)
        {
            // TODO: appears to only be used in caching mechanisms (OpenCompressed's first path)
            return new();
        }

        protected override IList<PatchEndpoint> GetEndpoints(string productCode, string region = "xx")
        {
            return [
                // todo parse from cdn endpoint
                new("blzddist1-a.akamaihd.net", "tpr/wow", "tpr/configs/data"),
                new("level3.blizzard.com", "tpr/wow", "tpr/configs/data"),
                new("us.cdn.blizzard.com", "tpr/wow", "tpr/configs/data")
            ];
        }
    }
}
