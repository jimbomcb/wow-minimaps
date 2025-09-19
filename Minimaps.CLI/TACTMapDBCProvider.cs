using DBCD.Providers;
using TACTSharp;

namespace Minimaps.Generator;

internal class TACTMapDBCProvider(BuildInstance buildInstance) : IDBCProvider
{
    public Stream StreamForTableName(string tableName, string build)
    {
        int fileDataID = tableName switch
        {
            "Map" => 1349477, // TODO: Confirm that this is stable enough to hard-code?
            _ => throw new NotImplementedException()
        };

        try
        {
            var fileBytes = buildInstance.OpenFileByFDID((uint)fileDataID);
            return new MemoryStream(fileBytes);
        }
        catch (Exception ex)
        {
            throw new Exception($"Unable to open file with fileDataID {fileDataID} for table {tableName}", ex);
        }
    }
}