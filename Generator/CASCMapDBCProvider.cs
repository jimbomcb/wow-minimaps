using CASCLib;
using DBCD.Providers;

namespace Minimaps.Generator;

// From https://github.com/Marlamin/WoWTools.Minimaps/blob/master/WoWTools.MinimapExtract/CASCDBCProvider.cs,
// todo: what are the magic numbers? what is 1349477? - I think it's the file ID of the map db2, seems stable enough to hard-code?
internal class CASCMapDBCProvider(CASCHandler casc) : IDBCProvider
{
	public Stream StreamForTableName(string tableName, string build)
	{
		int fileDataID = tableName switch
		{
			"Map" => 1349477,
			_ => throw new Exception("Don't know FileDataID for DBC " + tableName + ", add to switch please or implement listfile.csv reading. <3"), // todo: Listfile
		};
		return casc.OpenFile(fileDataID) ?? throw new Exception("Unable to open file with fileDataID " + fileDataID);
	}
}