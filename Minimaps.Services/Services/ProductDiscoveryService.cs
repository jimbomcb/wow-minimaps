using Minimaps.Database.Tables;
using Minimaps.Shared;
using Minimaps.Shared.RibbitClient;
using Npgsql;
using System.IO.Enumeration;

namespace Minimaps.Services;

/// <summary>
/// Monitors the TACT version server (aka "Ribbit") for new product versions
/// The backend will queue new scans of builds where necessary (and when we discover new decryption keys for known decrypted content)
/// </summary>
internal class ProductDiscoveryService : IntervalBackgroundService
{
    private class Configuration
    {
        /// <summary>
        /// List of products that we'll check the versions of, supports the ? and * wildcards 
        /// </summary>
        public List<string> Products { get; set; } = [];
        /// <summary>
        /// Exclude specific products from scanning (exact match)
        /// </summary>
        public Dictionary<string, string> ProductExclude { get; set; } = [];
    }

    private readonly Configuration _serviceConfig = new();
    private readonly ILogger<ProductDiscoveryService> _logger;
    private readonly NpgsqlDataSource _dataSource;

    public ProductDiscoveryService(ILogger<ProductDiscoveryService> logger, WebhookEventLog eventLog, IConfiguration configuration, NpgsqlDataSource dataSource) :
        base(logger, TimeSpan.FromSeconds(30), eventLog)
    {
        _logger = logger;
        _dataSource = dataSource;
        configuration.GetSection("Services:ProductDiscovery").Bind(_serviceConfig);
    }

    protected override async Task TickAsync(CancellationToken cancellationToken)
    {
        var ribbitClient = new RibbitClient(RibbitRegion.US);
        var tactSummary = await ribbitClient.SummaryAsync();
        var tactSequence = tactSummary.SequenceId; // todo: early-out if no change in sequence since last tick (also per-product?)
        _logger.LogInformation("Processing summary seq #{SequenceId} with {ProductCount} products", tactSequence, tactSummary.Data.Count);

        var productList = tactSummary.Data
            .Where(x => x.Flags != "cdn") // TODO: As far as I can tell there's nothing meaningful to be gained from tracking this here?
            .Where(prod => _serviceConfig.Products.Any(wildcard => FileSystemName.MatchesSimpleExpression(wildcard, prod.Name)))
            .ToList();

        _logger.LogInformation("Found {ProductCount} matching products to query", productList.Count);
        if (productList.Count == 0)
            return;

        // TODO: I think we need to handle BGDL? It would hit /bgdl not /versions
        var versionTasks = productList.Where(x => x.Flags == "").Select(async prod =>
        {
            try
            {
                var versions = await ribbitClient.VersionsAsync(prod.Name);
                if (prod.Seqn != versions.SequenceId)
                    _logger.LogError("Sequence ID mismatch? Summary page said {Expected} but actual version page had {Actual}?", prod.Seqn, versions.SequenceId);

                _logger.LogTrace("Found {total} versions for {Prod} @ {Seq}", versions.Data.Count, prod.Name, prod.Seqn);
                return versions.Data.Select(v => (Product: prod.Name, Version: v));
            }
            catch (ProductNotFoundException ex)
            {
                _logger.LogWarning("Product '{ProductName}' was listed on the service but has no versions available", ex.Product);
                return [];
            }
        });

        var foundVersions = (await Task.WhenAll(versionTasks))
            .SelectMany(vers => vers)
            .ToList();

        _logger.LogInformation("Discovered {FoundTotal} unique versions across {Products} products",
            foundVersions.DistinctBy(x => x.Version.VersionsName).Count(),
            productList.Count);

        if (foundVersions.Count == 0)
            return;

        try
        {
            // we're given the raw list of per-region products like the TACT version server typically provides,
            // group up the regions for each unique build_product entry
            var excludedProducts = _serviceConfig.ProductExclude.Keys.ToHashSet();
            var products = foundVersions.Where(x => !excludedProducts.Contains(x.Product)).GroupBy(
                k => (Version: BuildVersion.Parse(k.Version.VersionsName), k.Product, k.Version.BuildConfig, k.Version.CDNConfig, k.Version.ProductConfig),
                v => v.Version.Region)
                .ToList();

            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
            await using var initBatch = new NpgsqlBatch(conn, transaction);

            // Two groups of batch commands: 
            // - Get the list of known regions for the products we see
            // - Upsert build products for regions it wasn't seen in before
            foreach (var group in products)
            {
                var command = initBatch.CreateBatchCommand();
                command.CommandText = "SELECT config_regions FROM build_products WHERE build_id = @Version AND product = @Product" +
                    " AND config_build = @ConfigBuild AND config_cdn = @ConfigCdn AND config_product = @ConfigProduct FOR UPDATE;";
                command.Parameters.AddWithValue("Version", group.Key.Version);
                command.Parameters.AddWithValue("Product", group.Key.Product);
                command.Parameters.AddWithValue("ConfigBuild", group.Key.BuildConfig);
                command.Parameters.AddWithValue("ConfigCdn", group.Key.CDNConfig);
                command.Parameters.AddWithValue("ConfigProduct", group.Key.ProductConfig);
                initBatch.BatchCommands.Add(command);
            }

            foreach (var version in products.Select(x => x.Key.Version).Distinct())
            {
                var command = initBatch.CreateBatchCommand();
                command.CommandText = "INSERT INTO builds (id, version) VALUES ($1, $2) ON CONFLICT (id) DO NOTHING;";
                command.Parameters.AddWithValue(version);
                command.Parameters.AddWithValue((string)version);
                initBatch.BatchCommands.Add(command);
            }

            var existingRegionsMap = new Dictionary<(BuildVersion Version, string Product, string BuildConfig, string CDNConfig, string ProductConfig), string[]>();
            using (var reader = await initBatch.ExecuteReaderAsync(cancellationToken))
            {
                foreach (var group in products)
                {
                    string[] existingRegions = [];
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        var regionsValue = reader.GetValue(0);
                        if (regionsValue != DBNull.Value)
                            existingRegions = (string[])regionsValue;
                    }

                    existingRegionsMap[group.Key] = existingRegions;
                    await reader.NextResultAsync(cancellationToken);
                }
            }

            await using var insertBatch = new NpgsqlBatch(conn, transaction);
            foreach (var group in products)
            {
                var product = group.Key;
                var existingRegions = existingRegionsMap[product];
                var newRegions = group.Where(x => !existingRegions.Contains(x)).ToArray();
                if (newRegions.Length == 0)
                    continue; // build already tracked in all provided regions

                _logger.LogInformation("{Prod} {Version} - Adding regions: {Regions}", product.Product, product.Version, string.Join(", ", newRegions));
                // TODO: Trigger webhooks, notifications of a new build

                // upsert preserves the config_region order of new entries, appending newly seen regions for this product
                var command = insertBatch.CreateBatchCommand();
                command.CommandText = "INSERT INTO build_products (build_id, product, config_build, config_cdn, config_product, config_regions) " +
                    "VALUES (@Version, @Product, @ConfigBuild, @ConfigCdn, @ConfigProduct, @ConfigRegions) " +
                    "ON CONFLICT (build_id, product, config_build, config_cdn, config_product) " +
                    "DO UPDATE SET config_regions = build_products.config_regions || " +
                        "(SELECT array_agg(region) FROM unnest(EXCLUDED.config_regions) AS region WHERE region <> ALL(build_products.config_regions));";
                command.Parameters.AddWithValue("Version", product.Version);
                command.Parameters.AddWithValue("Product", product.Product);
                command.Parameters.AddWithValue("ConfigBuild", product.BuildConfig);
                command.Parameters.AddWithValue("ConfigCdn", product.CDNConfig);
                command.Parameters.AddWithValue("ConfigProduct", product.ProductConfig);
                command.Parameters.AddWithValue("ConfigRegions", newRegions);
                insertBatch.BatchCommands.Add(command);
            }

            // queue product scans for anything new
            foreach (var group in products)
            {
                var product = group.Key;

                var command = insertBatch.CreateBatchCommand();
                command.CommandText = "INSERT INTO build_scans (build_id, state) VALUES (@Version, @State) ON CONFLICT (build_id) DO NOTHING;";
                command.Parameters.AddWithValue("Version", product.Version);
                command.Parameters.AddWithValue("State", ScanState.Pending);
                insertBatch.BatchCommands.Add(command);
            }

            if (insertBatch.BatchCommands.Count > 0)
                await insertBatch.ExecuteNonQueryAsync(cancellationToken);
            else
                _logger.LogTrace("No changes found between current DB state and new published products");

            cancellationToken.ThrowIfCancellationRequested();

            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("Successfully processed {ProductCount} product versions", foundVersions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process products");
            throw;
        }
    }
}