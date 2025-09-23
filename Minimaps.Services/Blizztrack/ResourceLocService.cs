using Blizztrack.Framework.TACT.Implementation;
using Blizztrack.Framework.TACT.Resources;
using Polly;
using Polly.Retry;
using Polly.Telemetry;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;

using OwnedContentKey = Blizztrack.Framework.TACT.ContentKey;
using OwnedEncodingKey = Blizztrack.Framework.TACT.EncodingKey;
using ViewContentKey = Blizztrack.Framework.TACT.Views.ContentKey;
using ViewEncodingKey = Blizztrack.Framework.TACT.Views.EncodingKey;

namespace Minimaps.Services.Blizztrack;

public class ResourceLocService : IResourceLocator
{
    private readonly BlizztrackConfig _config = new();
    private readonly ILogger<ResourceLocService> _logger;
    private readonly IHttpClientFactory _clientFactory;
    private readonly ResiliencePipeline<HttpResponseMessage> _acquisitionPipeline;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private static readonly HttpStatusCode[] _retryStatusCodes =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.BadGateway,
        HttpStatusCode.GatewayTimeout,
        HttpStatusCode.RequestTimeout
    ];

    public ResourceLocService(IHttpClientFactory clientFactory, IServiceProvider sp, IConfiguration configuration)
    {
        _clientFactory = clientFactory;
        _logger = sp.GetRequiredService<ILogger<ResourceLocService>>();

        configuration.GetSection("Blizztrack").Bind(_config);
        if (string.IsNullOrWhiteSpace(_config.CachePath))

            throw new ArgumentException("Blizztrack:CachePath must be set in configuration");
        Directory.CreateDirectory(_config.CachePath);

        _acquisitionPipeline = BuildPipeline(sp);
    }

    private ResiliencePipeline<HttpResponseMessage> BuildPipeline(IServiceProvider sp) =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddConcurrencyLimiter(_config.ConcurrencyLimit, _config.ConcurrencyQueueLimit)
            .AddRateLimiter(new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = _config.RateLimitPermits,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = TimeSpan.FromSeconds(_config.RateLimitWindowSeconds),
                SegmentsPerWindow = _config.RateLimitSegments,
                QueueLimit = _config.QueueLimit,
                AutoReplenishment = true
            }))
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.FromSeconds(Math.Max(0.01, _config.RetryBaseDelaySeconds)),
                MaxRetryAttempts = _config.MaxRetryAttempts,
                ShouldHandle = args => args.Outcome switch
                {
                    { Exception: HttpRequestException } => PredicateResult.True(),
                    { Exception: TaskCanceledException } => PredicateResult.True(),
                    { Result: { StatusCode: var code } } when _retryStatusCodes.Contains(code) => PredicateResult.True(),
                    _ => PredicateResult.False()
                }
            })
            .ConfigureTelemetry(new TelemetryOptions { LoggerFactory = sp.GetRequiredService<ILoggerFactory>() })
            .Build();

    private static bool IsZero(in OwnedContentKey key) => key.AsSpan().Length == 0;
    private static bool IsZero(in OwnedEncodingKey key) => key.AsSpan().Length == 0;

    private string BuildCachePath(ResourceDescriptor descriptor, bool decompressed = false)
    {
        if (!IsZero(descriptor.ContentKey))
        {
            var ck = descriptor.ContentKey.AsHexString();
            return Path.Combine(_config.CachePath, "res", decompressed ? "decompressed" : "content", ck[..2], ck[2..4], ck);
        }

        if ((descriptor.Offset != 0 || descriptor.Length != 0) && !IsZero(descriptor.EncodingKey))
        {
            var ek = descriptor.EncodingKey.AsHexString();
            var segmentName = $"{ek}_{descriptor.Offset:x}_{descriptor.Length:x}";
            return Path.Combine(_config.CachePath, "res", decompressed ? "decompressed_segments" : "segments", ek[..2], ek[2..4], segmentName);
        }

        return Path.Combine(_config.CachePath, "res", descriptor.LocalPath);
    }

    public ResourceHandle OpenLocalHandle(ResourceDescriptor resourceDescriptor)
    {
        var localPath = BuildCachePath(resourceDescriptor);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        return new(localPath);
    }

    public ResourceHandle CreateLocalHandle(ResourceDescriptor resourceDescriptor, byte[] fileData)
    {
        var localPath = BuildCachePath(resourceDescriptor);
        var dir = Path.GetDirectoryName(localPath)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, Path.GetFileName(localPath) + $".tmp.{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(temp, fileData);
            File.Move(temp, localPath, true);
            return new(localPath);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public async Task<ResourceHandle> OpenHandle(ResourceDescriptor resourceDescriptor, CancellationToken stoppingToken = default)
    {
        var localHandle = OpenLocalHandle(resourceDescriptor);
        if (localHandle.Exists)
            return localHandle;

        var endpoints = GetEndpoints(resourceDescriptor.Product);
        if (endpoints.Count == 0)
            return default;

        var fileLock = _fileLocks.GetOrAdd(localHandle.Path, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(stoppingToken);
        try
        {
            var again = OpenLocalHandle(resourceDescriptor);
            if (again.Exists)
                return again;

            await using var remote = await Download(resourceDescriptor, endpoints, stoppingToken);
            if (remote == Stream.Null)
                return default;

            var dir = Path.GetDirectoryName(localHandle.Path)!;
            Directory.CreateDirectory(dir);
            var temp = localHandle.Path + $".tmp.{Guid.NewGuid():N}";
            try
            {
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 0, true))
                {
                    await remote.CopyToAsync(fs, stoppingToken);
                }
                File.Move(temp, localHandle.Path, true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
            return OpenLocalHandle(resourceDescriptor);
        }
        finally
        {
            fileLock.Release();
            if (fileLock.CurrentCount == 1)
            {
                _fileLocks.TryRemove(localHandle.Path, out _);
                fileLock.Dispose();
            }
        }
    }

    public async Task<Stream> OpenStream(ResourceDescriptor descriptor, CancellationToken stoppingToken = default)
    {
        var endpoints = GetEndpoints(descriptor.Product);
        if (endpoints.Count == 0)
            return Stream.Null;
        return await Download(descriptor, endpoints, stoppingToken);
    }

    // interface compressed handle methods (Views key types)
    public Task<ResourceHandle> OpenCompressedHandle(string productCode, in ViewEncodingKey encodingKey, CancellationToken stoppingToken = default)
        => OpenCompressedHandle(ResourceType.Data.ToDescriptor(productCode, encodingKey), stoppingToken);

    public Task<ResourceHandle> OpenCompressedHandle(string productCode, in ViewEncodingKey encodingKey, in ViewContentKey contentKey, CancellationToken stoppingToken = default)
    {
        var compressed = ResourceType.Data.ToDescriptor(productCode, encodingKey, contentKey);
        return OpenCompressedHandle(compressed, stoppingToken);
    }

    public async Task<ResourceHandle> OpenCompressedHandle(ResourceDescriptor compressedDescriptor, CancellationToken stoppingToken)
    {
        var decompressedDescriptor = ResourceType.Decompressed.ToDescriptor(compressedDescriptor.Product, compressedDescriptor.EncodingKey, compressedDescriptor.ContentKey);
        var localDecompressed = OpenLocalHandle(decompressedDescriptor);
        if (localDecompressed.Exists)
            return localDecompressed;

        var compressedHandle = await OpenHandle(compressedDescriptor, stoppingToken);
        if (!compressedHandle.Exists)
            return default;

        var decompressedData = BLTE.Parse(compressedHandle);
        CreateLocalHandle(decompressedDescriptor, decompressedData);
        return OpenLocalHandle(decompressedDescriptor);
    }

    public OwnedContentKey ResolveContentKey(in ViewEncodingKey encodingKey)
    {
        // TODO: appears to only be used in caching mechanisms (OpenCompressed's first path?), but working without for now..
        return new OwnedContentKey();
    }

    private IList<(string Host, string DataStem, string ConfigStem)> GetEndpoints(string productCode, string region = "xx") =>
        new List<(string, string, string)>
            // todo parse from cdn endpoint
            // Example CDN list:
            // us|tpr/wow|blzddist1-a.akamaihd.net level3.blizzard.com us.cdn.blizzard.com|http://blzddist1-a.akamaihd.net/?maxhosts=5&fallback=1 http://level3.blizzard.com/?maxhosts=8
            //   http://us.cdn.blizzard.com/?maxhosts=4&fallback=1 https://blzddist1-a.akamaihd.net/?maxhosts=4&fallback=1 https://level3.ssl.blizzard.com/?maxhosts=4&fallback=1
            //   https://us.cdn.blizzard.com/?maxhosts=4&fallback=1|tpr/configs/data

            // Each URL has max hosts, presumably max concurrent connections to that host, and a "fallback" presumably meaning anything without the fallback is prioriy for queries
            //   (ie level3.blizzard.com is current preferred)
            // TODO: see if other libraries at least try and follow that behaviour
        {
            ("level3.blizzard.com", "tpr/wow", "tpr/configs/data"),
            ("blzddist1-a.akamaihd.net", "tpr/wow", "tpr/configs/data"),
            ("us.cdn.blizzard.com", "tpr/wow", "tpr/configs/data")
        };

    private async Task<Stream> Download(ResourceDescriptor descriptor, IList<(string Host, string DataStem, string ConfigStem)> endpoints, CancellationToken token)
    {
        var client = _clientFactory.CreateClient();
        foreach (var ep in endpoints)
        {
            token.ThrowIfCancellationRequested();
            var request = BuildRequest(ep.Host, ep.DataStem, descriptor);
            try
            {
                var response = await _acquisitionPipeline.ExecuteAsync(async (ct) =>
                {
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }, token);
                if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.PartialContent)
                    return await response.Content.ReadAsStreamAsync(token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Endpoint fetch failed {Host} for {Path}", ep.Host, descriptor.RemotePath);
            }
        }
        return Stream.Null;
    }

    private static HttpRequestMessage BuildRequest(string host, string dataStem, ResourceDescriptor descriptor)
    {
        var url = $"http://{host}/{dataStem}/{descriptor.RemotePath}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (descriptor.Offset != 0 || descriptor.Length != 0)
        {
            long? end = descriptor.Length > 0 ? descriptor.Offset + descriptor.Length - 1 : null;

            // either bounded range or full remaining bytes
            request.Headers.Range = end.HasValue
                ? new RangeHeaderValue(descriptor.Offset, end)
                : new RangeHeaderValue(descriptor.Offset, null);
        }
        return request;
    }
}
