using Blizztrack.Framework.TACT;
using Blizztrack.Framework.TACT.Configuration;
using Blizztrack.Framework.TACT.Enums;
using Blizztrack.Framework.TACT.Implementation;
using Blizztrack.Framework.TACT.Resources;
using Minimaps.Shared.RibbitClient;
using System.Security.Cryptography;
using System.Text.Json;
using EncodingKeyView = Blizztrack.Framework.TACT.Views.EncodingKey;
using Index = Blizztrack.Framework.TACT.Implementation.Index;

namespace Minimaps.Services.Blizztrack;

public class FileSystemEncryptedException(string keyName) : Exception($"Build encrypted with key '{keyName}'")
{
    public string KeyName { get; } = keyName;
}

public class BlizztrackFSService(IRibbitClient ribbitClient, ResourceLocService resourceLocator)
{
    public async Task<Stream?> OpenStreamFDID(uint fdid, IFileSystem fs, bool validate = false, Locale localeFilter = Root.AllWoW, CancellationToken cancellation = default)
    {
        var descriptors = fs.OpenFDID(fdid, localeFilter);
        if (descriptors.Length == 0)
            return null;

        foreach (var descriptor in descriptors)
        {
            var dataHandle = await resourceLocator.OpenHandle(descriptor, cancellation);
            if (dataHandle.Exists)
            {
                var decoded = BLTE.Parse(dataHandle);
                if (validate)
                {
                    var computedHash = MD5.HashData(decoded);
                    if (!computedHash.SequenceEqual(descriptor.ContentKey.AsSpan()))
                        throw new Exception($"Data integrity error, requested fdid {fdid}, expected content hash {descriptor.ContentKey} but got {Convert.ToHexStringLower(computedHash)}");
                }

                return new MemoryStream(decoded);
            }
        }

        // todo: can we have descriptors to files that don't have a compression spec or accessible handle?
        throw new Exception($"Unable to open any of {descriptors.Length} descriptors for {fdid}");
    }

    public async Task<IFileSystem> ResolveFileSystem(string product, string buildConfig, string CDNConfig, string productConfig, CancellationToken cancellation)
    {
        // query the product list and populate CDN, not ideal but not too attached to this code as it sounds like blizztrack will change the handle stuff etc...
        if (!resourceLocator.HasProductCDNs(product))
        {
            var productCDNs = await ribbitClient.CDNsAsync(product);

            // level3.blizzard.com is backed by Akamai and pings an average of 1ms, max of 2ms so not getting much better than that
            // interesting how it can be quicker to stream data from Akamai servers than read from my NAS RAID...
            // I have yet to encounter data that exists on one CDN but not another (even the china specific CDNs)
            var cdnStems = productCDNs.Data.Select(x => (DataStem: x.Path, ConfigStem: x.ConfigPath)).Distinct();
            resourceLocator.SetProductCDNs(product, cdnStems.Select(x => new ResourceCDN("level3.blizzard.com", x.DataStem, x.ConfigStem)));
        }

        // temporary, apparently the resource handle stuff is going away anyway
        // we need to pull the product config from the config CDN path.
        await using var configStream = await resourceLocator.OpenConfigStream(product, productConfig);
        using (var jsonData = await JsonDocument.ParseAsync(configStream))
        {
            // expecting this not to change...
            if (!jsonData.RootElement.TryGetProperty("all", out var prodJsonAll))
                throw new Exception("Malformed product JSON, no 'all' for " + productConfig);

            if (!prodJsonAll.TryGetProperty("config", out var prodJsonConfig))
                throw new Exception("Malformed product JSON, no 'all:config' for " + productConfig);

            var requiresDecryptKey = prodJsonConfig.TryGetProperty("decryption_key_name", out var decKeyProperty);
            if (requiresDecryptKey)
            {
                // todo: do we have any of these to load? they're only referenced by name unlike tact keys?
                throw new FileSystemEncryptedException(decKeyProperty.GetString()!);
            }
        }

        var buildBytes = Convert.FromHexString(buildConfig);
        var serverBytes = Convert.FromHexString(CDNConfig);
        return await ResolveFileSystem(product, new EncodingKey(buildBytes), new EncodingKey(serverBytes), cancellation) ?? throw new Exception("Failed to resolve file system");
    }

    private async Task<IFileSystem> ResolveFileSystem(string productCode, EncodingKey buildConfiguration, EncodingKey serverConfiguration, CancellationToken stoppingToken)
    {
        var build = await OpenConfig<BuildConfiguration>(productCode, buildConfiguration, stoppingToken);
        var server = await OpenConfig<ServerConfiguration>(productCode, serverConfiguration, stoppingToken);
        return await OpenFileSystem(productCode, build, server, resourceLocator, stoppingToken);
    }

    private async Task<T> OpenConfig<T>(string productCode, EncodingKey encodingKey, CancellationToken stoppingToken) where T : class, IResourceParser<T>
    {
        var descriptor = ResourceType.Config.ToDescriptor(productCode, encodingKey, ContentKey.Zero);
        var openedHandle = await resourceLocator.OpenHandle(descriptor, stoppingToken);
        if (!openedHandle.Exists)
            throw new Exception($"Failed opening handle for config {encodingKey}");

        return T.OpenResource(openedHandle);
    }

    private async Task<IFileSystem> OpenFileSystem(string productCode, BuildConfiguration buildConfiguration,
        ServerConfiguration cdnConfiguration, ResourceLocService locator, CancellationToken stoppingToken = default)
    {
        // Based on how the Blizztrack project's FileSystemController, stream in the Encoding, Install, Indicies etc.
        if (cdnConfiguration.FileIndex.Size == 0)
            throw new Exception("Malformed file index?"); // only ever encountered when feeding encrypted invalid data 

        var encodingTask = resourceLocator.OpenCompressed<Encoding>(productCode,
            buildConfiguration.Encoding.Encoding.Key,
            buildConfiguration.Encoding.Content.Key,
            stoppingToken);

        var installTask = resourceLocator.OpenCompressed<Install>(productCode,
            buildConfiguration.Install.Encoding.Key,
            buildConfiguration.Install.Content.Key,
            stoppingToken);

        var indexTasks = cdnConfiguration.Archives.Select(async archive =>
        {
            var indexDesc = ResourceType.Indice.ToDescriptor(productCode, archive, ContentKey.Zero);
            var handle = await locator.OpenHandle(indexDesc, stoppingToken);
            return Index.Open(handle.ToMappedDataSource(), archive);
        }).ToList();

        var fileIndexDesc = ResourceType.Indice.ToDescriptor(productCode, cdnConfiguration.FileIndex.Key, ContentKey.Zero);
        var fileIndexHandle = await locator.OpenHandle(fileIndexDesc, stoppingToken);
        var fileIndex = Index.Open(fileIndexHandle.ToMappedDataSource(), buildConfiguration.Encoding.Encoding.Key);

        var compoundedIndex = new CompoundingIndex([.. await Task.WhenAll(indexTasks)]);
        var encoding = await encodingTask;
        var rootTask = ResolveRoot(productCode, encoding, compoundedIndex, fileIndex, buildConfiguration.Root, stoppingToken);

#pragma warning disable BT002
        return new FileSystem<CompoundingIndex>(productCode, compoundedIndex, encoding, await rootTask, await installTask, fileIndex);
#pragma warning restore BT002
    }

    // https://github.com/Warpten/Blizztrack/blob/fb485bece39f691f7e8d3b2547161f328df4d7dc/Blizztrack/Services/FileSystemSupplier.cs#L51
    private async Task<Root?> ResolveRoot(string productCode, Encoding encoding, CompoundingIndex compoundedIndex, IIndex? fileIndex, ContentKey root, CancellationToken stoppingToken)
    {
        var results = encoding.FindContentKey(root);
        if (results.Count == 0)
            return default;

        foreach (EncodingKeyView encodingKey in results.Keys)
        {
            var archiveInfo = compoundedIndex.FindEncodingKey(in encodingKey);
            if (!archiveInfo && fileIndex is not null)
                archiveInfo = fileIndex.FindEncodingKey(in encodingKey);

            if (archiveInfo)
                return await resourceLocator.OpenCompressed<Root>(productCode, archiveInfo.Archive, stoppingToken);
        }

        return default;
    }
}

