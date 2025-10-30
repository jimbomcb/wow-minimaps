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
    private readonly WebhookEventLog _eventLog;

    public ProductDiscoveryService(ILogger<ProductDiscoveryService> logger, WebhookEventLog eventLog, IConfiguration configuration, NpgsqlDataSource dataSource) :
        base(logger, TimeSpan.FromSeconds(30), eventLog)
    {
        _logger = logger;
        _dataSource = dataSource;
        _eventLog = eventLog;
        configuration.GetSection("Services:ProductDiscovery").Bind(_serviceConfig);
    }

    protected override async Task TickAsync(CancellationToken cancellationToken)
    {
        var ribbitClient = new RibbitClient(RibbitRegion.US);
        var tactSummary = await ribbitClient.SummaryAsync();
        var tactSequence = tactSummary.SequenceId; // todo: early-out if no change in sequence since last tick (also per-product?)
        _logger.LogTrace("Processing summary seq #{SequenceId} with {ProductCount} products", tactSequence, tactSummary.Data.Count);

        var productList = tactSummary.Data
            .Where(x => x.Flags != "cdn") // TODO: As far as I can tell there's nothing meaningful to be gained from tracking this here?
            .Where(prod => _serviceConfig.Products.Any(wildcard => FileSystemName.MatchesSimpleExpression(wildcard, prod.Name)))
            .ToList();

        _logger.LogTrace("Found {ProductCount} matching products to query", productList.Count);
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

        _logger.LogTrace("Discovered {FoundTotal} unique versions across {Products} products",
            foundVersions.DistinctBy(x => x.Version.VersionsName).Count(),
            productList.Count);

        if (foundVersions.Count == 0)
            return;

        try
        {
            var excludedProducts = _serviceConfig.ProductExclude.Keys.ToHashSet();
            var productGroups = foundVersions
                .Where(x => !excludedProducts.Contains(x.Product))
                .GroupBy(
                    k => (Version: BuildVersion.Parse(k.Version.VersionsName), k.Product),
                    v => (BuildConfig: v.Version.BuildConfig, CDN: v.Version.CDNConfig, ProductConfig: v.Version.ProductConfig, Region: v.Version.Region))
                .ToList();

            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

            // Ensure all builds exist and log new ones
            await using (var buildBatch = new NpgsqlBatch(conn, transaction))
            {
                foreach (var version in productGroups.Select(x => x.Key.Version).Distinct())
                {
                    var command = buildBatch.CreateBatchCommand();
                    command.CommandText = "INSERT INTO builds (id, version) VALUES ($1, $2) ON CONFLICT (id) DO NOTHING RETURNING id;";
                    command.Parameters.AddWithValue(version);
                    command.Parameters.AddWithValue((string)version);
                    buildBatch.BatchCommands.Add(command);
                }

                await using var reader = await buildBatch.ExecuteReaderAsync(cancellationToken);
                foreach (var version in productGroups.Select(x => x.Key.Version).Distinct())
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        var insertedId = reader.GetInt64(0);
                        _eventLog.Post($"@everyone New build discovered: {version}");
                    }
                    await reader.NextResultAsync(cancellationToken);
                }
            }

            var processedProducts = new Dictionary<Int64, (BuildVersion Version, string Product)>();

            foreach (var group in productGroups)
            {
                var productKey = group.Key;
                var sources = group.ToList();

                Int64 productId;
                string[] existingRegions = [];

                await using (var checkCmd = new NpgsqlCommand(
                    "SELECT id, config_regions FROM products " +
                    "WHERE build_id = @Version AND product = @Product " +
                    "FOR UPDATE", conn, transaction))
                {
                    checkCmd.Parameters.AddWithValue("Version", productKey.Version);
                    checkCmd.Parameters.AddWithValue("Product", productKey.Product);

                    await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        productId = reader.GetInt64(0);
                        var regionsValue = reader.GetValue(1);
                        if (regionsValue != DBNull.Value)
                            existingRegions = (string[])regionsValue;
                    }
                    else
                    {
                        productId = -1; // will be created
                    }
                }

                // Collect all regions from all sources
                var allRegions = sources.Select(x => x.Region).Distinct().ToHashSet();
                var newRegions = allRegions.Except(existingRegions).ToArray();

                bool isNewProduct = productId == -1;
                bool hasNewRegions = newRegions.Length > 0;

                if (isNewProduct || hasNewRegions)
                {
                    var combinedRegions = existingRegions.Concat(newRegions).ToArray();

                    if (isNewProduct)
                    {
                        await using var insertCmd = new NpgsqlCommand(
                            "INSERT INTO products (build_id, product, config_regions) " +
                            "VALUES (@Version, @Product, @ConfigRegions) " +
                            "RETURNING id", conn, transaction);
                        insertCmd.Parameters.AddWithValue("Version", productKey.Version);
                        insertCmd.Parameters.AddWithValue("Product", productKey.Product);
                        insertCmd.Parameters.AddWithValue("ConfigRegions", combinedRegions);

                        productId = (Int64)(await insertCmd.ExecuteScalarAsync(cancellationToken))!;

                        _logger.LogInformation("{Prod} {Version} - New product discovered with regions: {Regions}",
                            productKey.Product, productKey.Version, string.Join(", ", combinedRegions));
                        _eventLog.Post($"@everyone New product discovered: {productKey.Product} {productKey.Version} with regions: {string.Join(", ", combinedRegions)}");
                    }
                    else
                    {
                        // Update existing product with new regions
                        await using var updateCmd = new NpgsqlCommand(
                            "UPDATE products SET config_regions = @ConfigRegions WHERE id = @ProductId", conn, transaction);
                        updateCmd.Parameters.AddWithValue("ProductId", productId);
                        updateCmd.Parameters.AddWithValue("ConfigRegions", combinedRegions);
                        await updateCmd.ExecuteNonQueryAsync(cancellationToken);

                        _logger.LogInformation("{Prod} {Version} - Adding regions: {Regions}",
                            productKey.Product, productKey.Version, string.Join(", ", newRegions));
                        _eventLog.Post($"@everyone {productKey.Product} {productKey.Version} - Adding regions: {string.Join(", ", newRegions)}");
                    }

                    processedProducts[productId] = (productKey.Version, productKey.Product);
                }

                // Track sources
                var uniqueSources = sources
                    .GroupBy(s => (s.BuildConfig, s.CDN, s.ProductConfig))
                    .ToList();
                foreach (var sourceGroup in uniqueSources)
                {
                    var config = sourceGroup.Key;
                    var sourceRegions = sourceGroup.Select(s => s.Region).Distinct().ToArray();

                    // Upsert the source
                    await using var sourceCmd = new NpgsqlCommand(
                        "INSERT INTO product_sources (product_id, config_build, config_cdn, config_product, config_regions, first_seen) " +
                        "VALUES (@ProductId, @ConfigBuild, @ConfigCdn, @ConfigProduct, @ConfigRegions, timezone('utc', now())) " +
                        "ON CONFLICT (product_id, config_build, config_cdn, config_product) " +
                        "DO UPDATE SET config_regions = EXCLUDED.config_regions", conn, transaction);
                    sourceCmd.Parameters.AddWithValue("ProductId", productId);
                    sourceCmd.Parameters.AddWithValue("ConfigBuild", config.BuildConfig);
                    sourceCmd.Parameters.AddWithValue("ConfigCdn", config.CDN);
                    sourceCmd.Parameters.AddWithValue("ConfigProduct", config.ProductConfig);
                    sourceCmd.Parameters.AddWithValue("ConfigRegions", sourceRegions);
                    await sourceCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            // Queue product scans for new products
            if (processedProducts.Count > 0)
            {
                await using var scanBatch = new NpgsqlBatch(conn, transaction);
                foreach (var prodId in processedProducts.Keys)
                {
                    var command = scanBatch.CreateBatchCommand();
                    command.CommandText = "INSERT INTO product_scans (product_id) VALUES ($1) ON CONFLICT (product_id) DO NOTHING RETURNING product_id;";
                    command.Parameters.AddWithValue(prodId);
                    scanBatch.BatchCommands.Add(command);
                }

                await using var reader = await scanBatch.ExecuteReaderAsync(cancellationToken);
                foreach (var (prodId, (version, product)) in processedProducts)
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        var insertedProdId = reader.GetInt64(0);
                        _eventLog.Post($"@everyone  New scan queued: {product} {version}");
                    }
                    await reader.NextResultAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            _logger.LogTrace("Successfully processed {ProductCount} product versions", foundVersions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process products");
            throw;
        }
    }
}