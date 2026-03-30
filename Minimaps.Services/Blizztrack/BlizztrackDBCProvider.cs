using Blizztrack.Framework.TACT;
using Blizztrack.Framework.TACT.Enums;
using Blizztrack.Framework.TACT.Implementation;
using DBCD.Providers;
namespace Minimaps.Services.Blizztrack;

internal class BlizztrackDBCProvider(IFileSystem filesystem, ResourceLocService resourceLocator) : IDBCProvider
{
    public Stream StreamForTableName(string tableName, string build)
    {
        uint fileDataID = tableName switch
        {
            "Map" => 1349477,
            "AreaTable" => 1353545,
            _ => throw new NotImplementedException()
        };

        foreach (var entry in filesystem.OpenFDID(fileDataID, Locale.enUS))
        {
            var dataHandle = resourceLocator.OpenHandle(entry, CancellationToken.None).Result;
            if (dataHandle.Exists)
            {
                var bytes = BLTE.Parse(dataHandle);
                if (bytes == default)
                    throw new Exception("BLTE parsing error");

                return new MemoryStream(bytes);
            }
        }

        throw new Exception($"Unable to find file with fileDataID {fileDataID} for table {tableName}");
    }
}
