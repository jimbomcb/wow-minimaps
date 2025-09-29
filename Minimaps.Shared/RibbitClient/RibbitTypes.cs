namespace Minimaps.Shared.RibbitClient;

/// <summary>
/// Product!STRING:0|Seqn!DEC:4|Flags!STRING:0
/// </summary>
public readonly record struct Product(string Name, uint Seqn, string Flags);

/// <summary>
/// Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16
/// </summary>
public readonly record struct Version(string Region, string BuildConfig, string CDNConfig, string KeyRing, uint BuildId, string VersionsName, string ProductConfig);


/// <summary>
/// Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0
/// </summary>
public readonly record struct ProductCDN(string Name, string Path, string Hosts, string Servers, string ConfigPath);

