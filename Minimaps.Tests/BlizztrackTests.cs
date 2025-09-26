using Blizztrack.Framework.TACT.Implementation;
using Blizztrack.Framework.TACT.Resources;
using BLPSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minimaps.Services.Blizztrack;
using Minimaps.Shared;
using System.Security.Cryptography;

namespace Minimaps.Tests;

public class BlizztrackTests
{
    private static Dictionary<string, string?> GetTestConfigValues(string testCachePath)
    {
        Directory.CreateDirectory(testCachePath);

        return new()
        {
            ["Blizztrack:CachePath"] = testCachePath,
            ["Blizztrack:RateLimitPermits"] = "600",
            ["Blizztrack:RateLimitWindowSeconds"] = "60",
            ["Blizztrack:RateLimitSegments"] = "12",
            ["Blizztrack:QueueLimit"] = "2147483647",
            ["Blizztrack:ConcurrencyLimit"] = "3",
            ["Blizztrack:ConcurrencyQueueLimit"] = "2147483647",
            ["Blizztrack:MaxRetryAttempts"] = "3",
            ["Blizztrack:RetryBaseDelaySeconds"] = "1.0",
            ["Blizztrack:RetryMaxDelaySeconds"] = "30.0"
        };
    }

    [Fact]
    public async Task TestBlizztrack_MapDB2()
    {
        var configValues = GetTestConfigValues("C:\\temp\\lfs_test");

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

                var processedBLTE = BLTE.Parse(mapHandle);
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
        var configValues = GetTestConfigValues("C:\\temp\\lfs_test");

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
            var tactKeys = await TACTKeys.LoadAsync("C:\\temp\\lfs_test", logger);
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

                var processedBLTE = BLTE.Parse(mapHandle);
                Assert.NotNull(processedBLTE);
                Assert.True(processedBLTE.Length > 0, "Processed BLTE length is zero");
                logger.LogInformation("Processed BLTE length: {Length}", processedBLTE.Length);

                using var tempStream = new MemoryStream(processedBLTE);
                var reader = new WDTReader(tempStream);
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
        var configValues = GetTestConfigValues("C:\\temp\\lfs_test");

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
            var tactKeys = await TACTKeys.LoadAsync("C:\\temp\\lfs_test", logger);
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

                var processedBLTE = BLTE.Parse(handle);
                Assert.NotNull(processedBLTE);
                Assert.True(processedBLTE.Length > 0, "Processed BLTE length is zero");
                logger.LogInformation("Processed BLTE length: {Length}", processedBLTE.Length);

                try
                {
                    using var tempStream = new MemoryStream(processedBLTE);
                    using var blpFile = new BLPFile(tempStream);
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
        finally
        {
            serviceProvider.Dispose();
        }
    }

    [Fact]
    public async Task TestArchiveOffsetCachingProblem()
    {
        var testCachePath = "C:\\temp\\lfs_test_real";

        try
        {
            var cleanupDir = Path.Combine(testCachePath, "res", "data");
            if (Directory.Exists(cleanupDir))
                Directory.Delete(cleanupDir, true);
        }
        catch (Exception ex)
        {
        }

        var configValues = GetTestConfigValues(testCachePath);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddHttpClient();

        services.AddSingleton<IResourceLocator, ResourceLocService>();
        services.AddSingleton<BlizztrackFSService>();

        var serviceProvider = services.BuildServiceProvider();

        try
        {
            var resourceLocService = serviceProvider.GetRequiredService<IResourceLocator>();
            var blizztrackService = serviceProvider.GetRequiredService<BlizztrackFSService>();
            var logger = serviceProvider.GetRequiredService<ILogger<BlizztrackTests>>();

            const string product = "wow";
            const string buildConfig = "0a613ab3d004dd2b19c9c62637c9599a";
            const string cdnConfig = "20d8c0c2f193328ec144b3ecac49e574";

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var fileSystem = await blizztrackService.ResolveFileSystem(product, buildConfig, cdnConfig, cts.Token);

            // sample of fdids and their expected hashes, some of these files exist within the same archvie and raise the problem
            uint[] problematicFdids = { 204244, 204461, 204672, 204711 };
            string[] expectedHashes = {
                "26b259bf9aaabb823ab15715ad6b2ab0",
                "b6cbd64b0b5bbbcd67b644895fc09c65",
                "a2512439524e169654401034ecea98e0",
                "6e0c6024f4860b02a550fb9f72deaa46"
            };

            var descriptorInfos = new List<(uint fdid, string expectedHash, ResourceDescriptor descriptor, string localPath)>();
            for (int i = 0; i < problematicFdids.Length; i++)
            {
                var fdid = problematicFdids[i];
                var expectedHash = expectedHashes[i];
                var descriptors = fileSystem.OpenFDID(fdid, Blizztrack.Framework.TACT.Enums.Locale.enUS);

                if (descriptors.Length == 0)
                {
                    logger.LogWarning("No descriptors found for FDID {FDID}", fdid);
                    continue;
                }

                var descriptor = descriptors[0];
                var localPath = Path.Combine(testCachePath, "res", descriptor.LocalPath);

                descriptorInfos.Add((fdid, expectedHash, descriptor, localPath));
                logger.LogInformation("FDID {FDID}: LocalPath={LocalPath}, Offset={Offset}, Length={Length}, ExpectedHash={Hash}",
                    fdid, descriptor.LocalPath, descriptor.Offset, descriptor.Length, expectedHash);

                // if the actual expecte ckey doesn't match the ckey above then the testing files have been changed...
                if (descriptor.ContentKey.AsHexString() != expectedHash)
                {
                    logger.LogError("FDID {FDID} content key does not match expected hash! Expected {ExpectedHash}, got {ActualHash}",
                        fdid, expectedHash, descriptor.ContentKey);
                    Assert.Fail($"FDID {fdid} content key does not match expected hash! Expected {expectedHash}, got {descriptor.ContentKey}");
                }
            }

            // pull the OpenStream of the file, this should hit the CDN directly bypassing any local cache
            foreach (var (fdid, expectedHash, descriptor, localPath) in descriptorInfos)
            {
                logger.LogInformation("Opening stream for FDID {FDID}...", fdid);
                using var stream = await resourceLocService.OpenStream(descriptor, cts.Token);
                Assert.True(stream != Stream.Null, $"Stream for FDID {fdid} does not exist");

                var compressionSpec = fileSystem.GetCompressionSpec(descriptor.EncodingKey);
                Assert.NotNull(compressionSpec);

                using var decoded = await BLTE.Execute(stream, compressionSpec, stoppingToken: cts.Token);

                var computedHash = MD5.HashData(decoded);
                var computedHashString = Convert.ToHexStringLower(computedHash);

                if (computedHashString != expectedHash)
                {
                    // shouldn't happen unless the underlying files change, which is not the actual problem being tested here
                    Assert.Fail($"Data corruption on FDID {fdid}: expected hash {expectedHash}, got {computedHashString}");
                }
            }

            // now do the same but through the OpenHandle path, which when used with the stock AbstractResourceLocatorService implementation
            // results in overlapping data that shares an archive
            foreach (var (fdid, expectedHash, descriptor, localPath) in descriptorInfos)
            {
                logger.LogInformation("Opening handle for FDID {FDID}...", fdid);
                var handle = await resourceLocService.OpenHandle(descriptor, cts.Token);
                var decoded = BLTE.Parse(handle);

                var computedHash = MD5.HashData(decoded);
                var computedHashString = Convert.ToHexStringLower(computedHash);
                if (computedHashString != expectedHash)
                {
                    // hitting this corruption assert at the time of writing
                    Assert.Fail($"Data corruption on FDID {fdid}: expected hash {expectedHash}, got {computedHashString}");
                }
            }
        }
        finally
        {
            serviceProvider.Dispose();
        }
    }
}
