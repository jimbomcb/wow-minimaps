using Blizztrack.Framework.TACT;
using Blizztrack.Framework.TACT.Enums;
using Blizztrack.Framework.TACT.Implementation;
using Blizztrack.Framework.TACT.Resources;
using DBCD.Providers;
namespace Minimaps.Services.Blizztrack;

internal class BlizztrackDBCProvider(IFileSystem filesystem, IResourceLocator resourceLocator) : IDBCProvider
{
    public Stream StreamForTableName(string tableName, string build)
    {
        uint fileDataID = tableName switch
        {
            "Map" => 1349477, // TODO: Confirm that this is stable enough to hard-code?
            _ => throw new NotImplementedException()
        };

        try
        {
            foreach (var entry in filesystem.OpenFDID(fileDataID, Locale.enUS)) // todo: maybe just makes more sense to move locale to the whole filesystem level
            {
                var compressionSpec = filesystem.GetCompressionSpec(entry.EncodingKey);
                if (compressionSpec is null)
                    continue;

                var dataHandle = resourceLocator.OpenHandle(entry, CancellationToken.None).Result;
                if (dataHandle.Exists)
                    return BLTE.Execute(dataHandle.ToStream(), compressionSpec).Result;
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Unable to open file with fileDataID {fileDataID} for table {tableName}", ex);
        }

        throw new Exception($"Unable to find file with fileDataID {fileDataID} for table {tableName}");
    }
}
