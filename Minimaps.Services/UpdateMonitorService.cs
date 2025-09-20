using Minimaps.Shared;
using Minimaps.Shared.BackendDto;
using RibbitClient;
using TACTSharp;

namespace Minimaps.Services;

/// <summary>
/// Monitor for the publishing of new WoW products (wow, wowt, wow_beta, etc) and trigger processing when a new build is detected.
/// Builds are treated unique per build version + product name, I don't expect that map changes happen when switching a build from PTR to main without
/// a build change, but I could be wrong, so this is the safest approach...
/// The backend will be queried for unregistered (or registered but unregistered) builds, and begin processing the tile data into the backend tile store.
/// Once all tiles have been published, the build is marked as processed (given it passes a sanity check of everything expected existing in the store etc).
/// </summary>
internal class UpdateMonitorService :
    IntervalBackgroundService
{
    private class Configuration
    { 
        public string CachePath { get; set; } = "./cache";
        public List<string> Products { get; set; } = [];
        public List<string> AdditionalCDNs { get; set; } = [];
    }
    private readonly record struct ProductVersion(string BuildConfig, string CDNConfig, List<string> Regions);

    private readonly Configuration _serviceConfig = new();
    private readonly ILogger<UpdateMonitorService> _logger;
    private BuildInstance? _tactClient = null;
    private readonly BackendClient _backendClient;

    public UpdateMonitorService(ILogger<UpdateMonitorService> logger, WebhookEventLog eventLog, IConfiguration configuration, BackendClient backendClient) : 
        base(logger, TimeSpan.FromSeconds(30), eventLog)
    {
        _logger = logger;
        configuration.GetSection("Services:UpdateMonitor").Bind(_serviceConfig);
        _backendClient = backendClient;
    }

    protected override async Task TickAsync(CancellationToken cancellationToken)
    {
        var loadKeyTask = TACTKeys.LoadAsync(_serviceConfig.CachePath, _logger);
        var ribbitClient = new RibbitClient.RibbitClient(RibbitRegion.US);

        var tactSummary = await ribbitClient.SummaryAsync();
        var tactSequence = tactSummary.SequenceId; // todo: early-out if no change in sequence since last tick
        _logger.LogInformation("Processing summary seq #{SequenceId} with {ProductCount} products", tactSequence, tactSummary.Data.Count);

        // todo: check the summary sequence IDs of the individual products for filtering

        _tactClient = new BuildInstance();
        _tactClient.Settings.CacheDir = _serviceConfig.CachePath;
        foreach (var additionalCdn in _serviceConfig.AdditionalCDNs)
        {
            _tactClient.Settings.AdditionalCDNs.Add(additionalCdn);
            _logger.LogInformation("Added additional CDN: {cdn}", additionalCdn);
        }
        _tactClient.Settings.BaseDir = "C:\\World of Warcraft";
        _tactClient.Settings.TryCDN = true;
        //_tactClient.LoadConfigs(productEntry.BuildConfig, productEntry.CDNConfig);
        //_tactClient.Load();

        foreach (var entry in await loadKeyTask)
        {
            KeyService.SetKey(entry.KeyName, entry.KeyValue);
        }

        // gather all the latest products & their versions
        var products = new Dictionary<(string product, string version), ProductVersion>();
        foreach (var product in _serviceConfig.Products)
        {
            try
            {
                var versionsResponse = await ribbitClient.VersionsAsync(product);
                foreach (var version in versionsResponse.Data)
                {
                    var key = (product, version.VersionsName);
                    if (!products.ContainsKey(key))
                        products[key] = new(version.BuildConfig, version.CDNConfig, []);

                    products[key].Regions.Add(version.Region);
                }
            }
            catch (ProductNotFoundException ex)
            {
                _logger.LogWarning("Product {Product} not found: {Message}", product, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing versions for {Product}", product);
            }
        }

        _logger.LogInformation("Discovered {Versions} versions across {Products} products", products.Count, _serviceConfig.Products.Count);

        var newBuilds = await _backendClient.PostAsync<DiscoveredRequestDto>("publish/discovered", new DiscoveredRequestDto
        {
            Entries = [.. products.Keys.Select(k => new DiscoveredRequestDtoEntry(k.product,k.version))]
        });

        _logger.LogInformation("{NewBuilds} builds not yet published", newBuilds.Entries.Count);
    }
}