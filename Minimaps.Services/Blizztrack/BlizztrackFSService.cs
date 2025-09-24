using Blizztrack.Framework.TACT;
using Blizztrack.Framework.TACT.Configuration;
using Blizztrack.Framework.TACT.Enums;
using Blizztrack.Framework.TACT.Implementation;
using Blizztrack.Framework.TACT.Resources;
using System.Security.Cryptography;
using EncodingKeyView = Blizztrack.Framework.TACT.Views.EncodingKey;
using Index = Blizztrack.Framework.TACT.Implementation.Index;

namespace Minimaps.Services.Blizztrack;

public class BlizztrackFSService(IResourceLocator resourceLocator)
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

    public async Task<IFileSystem> ResolveFileSystem(string product, string buildConfig, string CDNConfig, CancellationToken cancellation)
    {
        var buildBytes = Convert.FromHexString(buildConfig);
        var serverBytes = Convert.FromHexString(CDNConfig);
        return await ResolveFileSystem(product, new EncodingKey(buildBytes), new EncodingKey(serverBytes), cancellation) ?? throw new Exception("Failed to resolve file system");
    }

    private async Task<IFileSystem> ResolveFileSystem(string productCode, EncodingKey buildConfiguration, EncodingKey serverConfiguration, CancellationToken stoppingToken)
    {
        var buildTask = OpenConfig<BuildConfiguration>(productCode, buildConfiguration, stoppingToken);
        var serverTask = OpenConfig<ServerConfiguration>(productCode, serverConfiguration, stoppingToken);
        return await OpenFileSystem(productCode, await buildTask, await serverTask, resourceLocator, stoppingToken);
    }

    private async Task<T> OpenConfig<T>(string productCode, EncodingKey encodingKey, CancellationToken stoppingToken) where T : class, IResourceParser<T>
    {
        var descriptor = ResourceType.Config.ToDescriptor(productCode, encodingKey, ContentKey.Zero);
        return T.OpenResource(await resourceLocator.OpenHandle(descriptor, stoppingToken));
    }

    private async Task<IFileSystem> OpenFileSystem(string productCode, BuildConfiguration buildConfiguration,
        ServerConfiguration cdnConfiguration, IResourceLocator locator, CancellationToken stoppingToken = default)
    {
        // Based on how the Blizztrack project's FileSystemController, stream in the Encoding, Install, Indicies etc.

        if (cdnConfiguration.FileIndex.Size == 0)
            throw new Exception("TODO"); // not yet encountered? is it ever possible?

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

