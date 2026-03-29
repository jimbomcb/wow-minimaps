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

public readonly record struct ResourceCDN(string Host, string DataStem, string ConfigStem);
public class ResourceLocService : IResourceLocator
{
    private readonly BlizztrackConfig _config = new();
    private readonly ILogger<ResourceLocService> _logger;
    private readonly IHttpClientFactory _clientFactory;
    private readonly ResiliencePipeline<HttpResponseMessage> _acquisitionPipeline;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    // per-scan caches, cleared via ResetDownloadCaches() at scan start
    private readonly ConcurrentDictionary<string, byte> _knownMissing = new(); // (host|remotePath) combos that returned 403/404
    private readonly ConcurrentDictionary<string, int> _hostFailures = new(); // consecutive connection failures per host
    private const int HOST_FAILURE_THRESHOLD = 10;

    /// <summary>
    /// Clear result cache, called at the start of each scan
    /// </summary>
    public void ResetDownloadCaches()
    {
        _knownMissing.Clear();
        _hostFailures.Clear();
    }

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

    private List<ResourceCDN> GetEndpoints(string productCode)
    {
        if (!HasProductCDNs(productCode))
            throw new Exception("Attempting to get endpoints but no registered CDNs for " + productCode);

        return _resourceCDNs[productCode];
    }

    private async Task<Stream> Download(ResourceDescriptor descriptor, List<ResourceCDN> endpoints, CancellationToken token)
    {
        var client = _clientFactory.CreateClient();
        var failedHosts = new List<string>();
        foreach (var ep in endpoints)
        {
            token.ThrowIfCancellationRequested();

            // known to not have this file (prior 403/404)
            var missingKey = $"{ep.Host}|{descriptor.RemotePath}";
            if (_knownMissing.ContainsKey(missingKey))
            {
                failedHosts.Add(ep.Host);
                continue;
            }

            // too many connection failures (broken/unreachable endpoint)
            if (_hostFailures.TryGetValue(ep.Host, out var failures) && failures >= HOST_FAILURE_THRESHOLD)
            {
                failedHosts.Add(ep.Host);
                continue;
            }

            try
            {
                var response = await _acquisitionPipeline.ExecuteAsync(async (ct) =>
                {
                    var request = BuildRequest(ep.Host, ep.DataStem, descriptor);
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }, token);
                if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.PartialContent)
                {
                    _hostFailures.TryRemove(ep.Host, out _); // reset failure count
                    if (failedHosts.Count > 0)
                        _logger.LogInformation("Fetched {Path} from {Host} (failed: {FailedHosts})", descriptor.RemotePath, ep.Host, string.Join(", ", failedHosts));
                    return await response.Content.ReadAsStreamAsync(token);
                }

                // 403/404 = file definitively not on this CDN, remember it
                // (403 because level3 is backed by S3 and blizz's AWS IAM role doesn't have ListBucket,
                //  missing object S3 requests redirect to a "ListBucket" command and it has no perms to do that,
                //  so instead of just saying that the object doesn't exist. Normal S3 fun.)
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    if (_knownMissing.TryAdd(missingKey, 0))
                        _logger.LogInformation("{Path} not on {Host} ({Status}), will skip for future requests", descriptor.RemotePath, ep.Host, (int)response.StatusCode);
                }

                failedHosts.Add(ep.Host);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                var count = _hostFailures.AddOrUpdate(ep.Host, 1, (_, c) => c + 1);
                if (count == HOST_FAILURE_THRESHOLD)
                    _logger.LogWarning("Host {Host} has failed {Count} consecutive times, skipping for remainder of scan", ep.Host, count);
                failedHosts.Add(ep.Host);
            }
        }

        if (failedHosts.Count > 0)
            _logger.LogDebug("All endpoints failed for {Path}: {Hosts}", descriptor.RemotePath, string.Join(", ", failedHosts));
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

    private readonly Dictionary<string, List<ResourceCDN>> _resourceCDNs = [];

    public bool HasProductCDNs(string product)
    {
        return _resourceCDNs.ContainsKey(product) && _resourceCDNs[product].Count > 0;
    }

    public void SetProductCDNs(string product, IEnumerable<ResourceCDN> cdnEntries)
    {
        if (!_resourceCDNs.TryGetValue(product, out var entries))
        {
            _resourceCDNs.Add(product, []);
            entries = _resourceCDNs[product];
        }

        entries.AddRange(cdnEntries);
    }

    /// <summary>
    /// Probably temporary way to grab something using the CDN config stem for a given product
    /// </summary>
    public async Task<Stream> OpenConfigStream(string product, string path)
    {
        var productCDN = _resourceCDNs[product] ?? throw new Exception("No CDNs tracked for product " + product);

        var localPath = Path.Combine(_config.CachePath, "configdata", product, path[..2], path[2..4], path);

        if (File.Exists(localPath))
            return new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var fileLock = _fileLocks.GetOrAdd(localPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync();
        try
        {
            if (File.Exists(localPath))
                return new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            foreach (var ep in productCDN)
            {
                var client = _clientFactory.CreateClient();
                var url = $"http://{ep.Host}/{ep.ConfigStem}/{path[..2]}/{path[2..4]}/{path}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                try
                {
                    var response = await _acquisitionPipeline.ExecuteAsync(async (ct) =>
                    {
                        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    }, CancellationToken.None);

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var content = await response.Content.ReadAsByteArrayAsync();

                        var dir = Path.GetDirectoryName(localPath)!;
                        Directory.CreateDirectory(dir);
                        var temp = localPath + $".tmp.{Guid.NewGuid():N}";
                        try
                        {
                            await File.WriteAllBytesAsync(temp, content);
                            File.Move(temp, localPath, true);
                        }
                        catch
                        {
                            try
                            {
                                if (File.Exists(temp))
                                    File.Delete(temp);
                            }
                            catch { }
                            throw;
                        }

                        return new MemoryStream(content);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Config fetch failed {Host} for {Path}", ep.Host, path);
                }
            }
        }
        finally
        {
            fileLock.Release();
            if (fileLock.CurrentCount == 1)
            {
                _fileLocks.TryRemove(localPath, out _);
                fileLock.Dispose();
            }
        }

        return Stream.Null;
    }
}
