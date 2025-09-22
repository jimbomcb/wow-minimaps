using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minimaps.Services.Blizztrack;
using Blizztrack.Framework.TACT.Resources;
using Blizztrack.Framework.TACT.Implementation;
using Minimaps.Shared;
using BLPSharp;

namespace Minimaps.Tests;

public class BlizztrackTests
{
    [Fact]
    public async Task TestBlizztrack_MapDB2()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Blizztrack:CachePath"] = "C:\\temp\\lfs",
            ["Blizztrack:RateLimitPermits"] = "10",
            ["Blizztrack:RateLimitWindowSeconds"] = "60",
            ["Blizztrack:RateLimitSegments"] = "12",
            ["Blizztrack:QueueLimit"] = "2147483647",
            ["Blizztrack:ConcurrencyLimit"] = "3",
            ["Blizztrack:ConcurrencyQueueLimit"] = "2147483647",
            ["Blizztrack:MaxRetryAttempts"] = "3",
            ["Blizztrack:RetryBaseDelaySeconds"] = "1.0",
            ["Blizztrack:RetryMaxDelaySeconds"] = "30.0"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient();

        services.AddSingleton<IResourceLocator, ResourceLocService>();
        services.AddSingleton<BlizztrackFSService>();

        var serviceProvider = services.BuildServiceProvider();

        try
        {
            var resourceLocService = serviceProvider.GetRequiredService<IResourceLocator>();
            var blizztrackService = serviceProvider.GetRequiredService<BlizztrackFSService>();
            var logger = serviceProvider.GetRequiredService<ILogger<BlizztrackTests>>();

            // todo: will break once the products cycle, need to grab a known active product from version server
            const string product = "wow";
            const string buildConfig = "0a613ab3d004dd2b19c9c62637c9599a";
            const string cdnConfig = "20d8c0c2f193328ec144b3ecac49e574";

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var fileSystem = await blizztrackService.ResolveFileSystem(product, buildConfig, cdnConfig, cts.Token);

            const uint testFileId = 1349477; // Map DBC
            foreach (var entry in fileSystem.OpenFDID(testFileId, Blizztrack.Framework.TACT.Enums.Locale.enUS))
            {
                var mapHandle = await resourceLocService.OpenHandle(entry, cts.Token);
                Assert.True(mapHandle.Exists, "Map handle not found");

                var compressionSpec = fileSystem.GetCompressionSpec(entry.EncodingKey);
                if (compressionSpec is null)
                    continue;

                var processedBLTE = await BLTE.Execute(mapHandle.ToStream(), compressionSpec, stoppingToken: cts.Token);
                Assert.NotNull(processedBLTE);
                Assert.True(processedBLTE.Length > 0, "Processed BLTE length is zero");
                logger.LogInformation("Processed BLTE length: {Length}", processedBLTE.Length);
            }
        }
        finally
        {
            serviceProvider.Dispose();
        }
    }

    [Fact]
    public async Task TestBlizztrack_EncryptedMap()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Blizztrack:CachePath"] = "C:\\temp\\lfs",
            ["Blizztrack:RateLimitPermits"] = "10",
            ["Blizztrack:RateLimitWindowSeconds"] = "60",
            ["Blizztrack:RateLimitSegments"] = "12",
            ["Blizztrack:QueueLimit"] = "2147483647",
            ["Blizztrack:ConcurrencyLimit"] = "3",
            ["Blizztrack:ConcurrencyQueueLimit"] = "2147483647",
            ["Blizztrack:MaxRetryAttempts"] = "3",
            ["Blizztrack:RetryBaseDelaySeconds"] = "1.0",
            ["Blizztrack:RetryMaxDelaySeconds"] = "30.0"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient();

        services.AddSingleton<IResourceLocator, ResourceLocService>();
        services.AddSingleton<BlizztrackFSService>();

        var serviceProvider = services.BuildServiceProvider();

        try
        {
            // Load TACT keys before testing
            var logger = serviceProvider.GetRequiredService<ILogger<BlizztrackTests>>();
            var tactKeys = await TACTKeys.LoadAsync("C:\\temp\\lfs", logger);
            foreach (var key in tactKeys)
            {
                TACTKeyService.SetKey(key.KeyName, key.KeyValue);
            }
            logger.LogInformation("Loaded {KeyCount} TACT keys", tactKeys.Count);

            var resourceLocService = serviceProvider.GetRequiredService<IResourceLocator>();
            var blizztrackService = serviceProvider.GetRequiredService<BlizztrackFSService>();

            // todo: will break once the products cycle, need to grab a known active product from version server
            const string product = "wow";
            const string buildConfig = "0a613ab3d004dd2b19c9c62637c9599a";
            const string cdnConfig = "20d8c0c2f193328ec144b3ecac49e574";

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var fileSystem = await blizztrackService.ResolveFileSystem(product, buildConfig, cdnConfig, cts.Token);

            const uint testFileId = 3182457; // known-encrypted file: "b:{256K*=e:{F21C5CA430F434D1,8E085D49,z}}"
            foreach (var entry in fileSystem.OpenFDID(testFileId, Blizztrack.Framework.TACT.Enums.Locale.enUS))
            {
                var mapHandle = await resourceLocService.OpenHandle(entry, cts.Token);
                Assert.True(mapHandle.Exists, "Map handle not found");

                var compressionSpec = fileSystem.GetCompressionSpec(entry.EncodingKey);
                if (compressionSpec is null)
                    continue;

                var processedBLTE = await BLTE.Execute(mapHandle.ToStream(), compressionSpec, stoppingToken: cts.Token);
                Assert.NotNull(processedBLTE);
                Assert.True(processedBLTE.Length > 0, "Processed BLTE length is zero");
                logger.LogInformation("Processed BLTE length: {Length}", processedBLTE.Length);

                var reader = new WDTReader(processedBLTE);
                var minimapEntries = reader.ReadMinimapTiles();
                Assert.True(minimapEntries.Count > 0);
            }
        }
        finally
        {
            serviceProvider.Dispose();
        }
    }

    [Fact]
    public async Task TestBlizztrack_EncryptedBLP()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Blizztrack:CachePath"] = "C:\\temp\\lfs",
            ["Blizztrack:RateLimitPermits"] = "10",
            ["Blizztrack:RateLimitWindowSeconds"] = "60",
            ["Blizztrack:RateLimitSegments"] = "12",
            ["Blizztrack:QueueLimit"] = "2147483647",
            ["Blizztrack:ConcurrencyLimit"] = "3",
            ["Blizztrack:ConcurrencyQueueLimit"] = "2147483647",
            ["Blizztrack:MaxRetryAttempts"] = "3",
            ["Blizztrack:RetryBaseDelaySeconds"] = "1.0",
            ["Blizztrack:RetryMaxDelaySeconds"] = "30.0"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient();

        services.AddSingleton<IResourceLocator, ResourceLocService>();
        services.AddSingleton<BlizztrackFSService>();

        var serviceProvider = services.BuildServiceProvider();

        try
        {
            // Load TACT keys before testing
            var logger = serviceProvider.GetRequiredService<ILogger<BlizztrackTests>>();
            var tactKeys = await TACTKeys.LoadAsync("C:\\temp\\lfs", logger);
            foreach (var key in tactKeys)
            {
                TACTKeyService.SetKey(key.KeyName, key.KeyValue);
            }
            logger.LogInformation("Loaded {KeyCount} TACT keys", tactKeys.Count);

            var resourceLocService = serviceProvider.GetRequiredService<IResourceLocator>();
            var blizztrackService = serviceProvider.GetRequiredService<BlizztrackFSService>();

            // todo: will break once the products cycle, need to grab a known active product from version server
            const string product = "wow";
            const string buildConfig = "0a613ab3d004dd2b19c9c62637c9599a";
            const string cdnConfig = "20d8c0c2f193328ec144b3ecac49e574";

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var fileSystem = await blizztrackService.ResolveFileSystem(product, buildConfig, cdnConfig, cts.Token);

            const uint testFileId = 3182843; // known-encrypted file: "b:{256K*=e:{F21C5CA430F434D1,40821F74,z}}
            foreach (var entry in fileSystem.OpenFDID(testFileId, Blizztrack.Framework.TACT.Enums.Locale.enUS))
            {
                var handle = await resourceLocService.OpenHandle(entry, cts.Token);
                Assert.True(handle.Exists, "handle not found");

                var compressionSpec = fileSystem.GetCompressionSpec(entry.EncodingKey);
                if (compressionSpec is null)
                    continue;

                var dataHandle = await resourceLocService.OpenHandle(entry, cts.Token);
                if (dataHandle.Exists)
                {
                    var processedBLTE = await BLTE.Execute(dataHandle.ToStream(), compressionSpec, stoppingToken: cts.Token);
                    Assert.NotNull(processedBLTE);
                    Assert.True(processedBLTE.Length > 0, "Processed BLTE length is zero");
                    logger.LogInformation("Processed BLTE length: {Length}", processedBLTE.Length);

                    try
                    {
                        using var blpFile = new BLPFile(processedBLTE);
                        var mapBytes = blpFile.GetPixels(0, out int width, out int height);

                        logger.LogInformation("Successfully decoded BLP: {Width}x{Height}, {PixelCount} pixels", width, height, width * height);
                    }
                    catch (Exception bex)
                    {
                        logger.LogError(bex, "Failed to decode BLP: {Message}", bex.Message);
                        throw;
                    }
                }
            }
        }
        finally
        {
            serviceProvider.Dispose();
        }
    }
}
