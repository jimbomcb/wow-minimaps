using Minimaps.Shared;
using NodaTime;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

internal class Build
{
    /// <summary>
    /// Primary key: build version encoded into a sortable int64
    /// Bit-packed to: expansion(11) | major(10) | minor(10) | build(32)
    /// </summary>
    public BuildVersion id { get; set; }

    /// <summary>
    /// Full version string "11.0.7.58046" [expansion].[major].[minor].[build]
    /// </summary>
    public string version { get; set; }

    /// <summary>
    /// First time this build was seen by the scanner
    /// </summary>
    public Instant discovered { get; set; }
}

/// <summary>
/// Should keep in sync with the scan_state snake-case DB version 
/// </summary>
public enum ScanState
{
    Pending,
    /// <summary>
    /// Scanning failed with an exception, exception column should have error
    /// </summary>
    Exception,
    /// <summary>
    /// We couldn't even access the build, it's protected by an armadillo key (named in encrypted_key)
    /// </summary>
    EncryptedBuild,
    /// <summary>
    /// We could access the build but the map database itself was encrypted (TACT key name hex stored in encrypted_key)
    /// </summary>
    EncryptedMapDatabase,
    /// <summary>
    /// There were some encrypted maps, the map IDs and associated missing key names are stored inside encrypted_maps.
    /// If we discover these keys at a later point we can re-scan for a more complete build (assuming CDN still has the data).
    /// </summary>
    PartialDecrypt,
    /// <summary>
    /// All maps referenced in the database were decrypted successfully
    /// </summary>
    FullDecrypt
}

/// <summary>
/// Each new build (not historical builds) will have an associated scan entry
/// </summary>
internal class BuildScan
{
    public BuildVersion build_id { get; set; }
    public ScanState state { get; set; }
    public Instant first_seen { get; set; }
    public Instant last_scanned { get; set; }
    public Period scan_time { get; set; }

    /// <summary>
    /// Scanner exception when in the Exception state
    /// </summary>
    public string exception { get; set; }

    /// <summary>
    /// Key needed to process this build if we're in the EncryptedBuild state
    /// </summary>
    public string encrypted_key { get; set; }

    /// <summary>
    /// JSONB serialized list of encrypted maps and their decryption key name
    /// IF we're in the PartialDecrypt state
    /// {
    ///     map_id: "key_name",
    ///     ...
    /// }
    /// </summary>
    public string encrypted_maps { get; set; }

    /// <summary>
    /// The specific build/CDN/product used during this scan (FK to BuildProduct)
    /// </summary>
    public string config_build { get; set; }
    public string config_cdn { get; set; }
    public string config_product { get; set; }
}

/// <summary>
/// Represents product releases for builds (retail, ptr, beta, etc.)
/// Primary key: (build_id, product, config_build, config_cdn, config_product)
/// </summary>
public class BuildProduct
{
    /// <summary>
    /// References Build.id
    /// </summary>
    public BuildVersion build_id { get; set; }

    /// <summary>
    /// Product name (retail, ptr, beta, etc.)
    /// </summary>
    public string product { get; set; }

    public string config_build { get; set; }
    public string config_cdn { get; set; }
    public string config_product { get; set; }
    /// <summary>
    /// set of string region tags in which this cdn/build/product was seen
    /// </summary>
    public string[] config_regions { get; set; }

    public Instant first_seen { get; set; }
}

internal class Map
{
    /// <summary>
    /// Map ID from the game database
    /// </summary>
    public int id { get; set; }

    /// <summary>
    /// JSONB string of latest versions raw database row
    /// </summary>
    public string json { get; set; }

    /// <summary>
    /// Most recent directory
    /// </summary>
    public string directory { get; set; }

    /// <summary>
    /// Most recent name
    /// </summary>
    public string name { get; set; }

    public BuildVersion first_version { get; set; }
    public BuildVersion last_version { get; set; }

    /// <summary>
    /// JSONB object consisting of: 
    /// {
    ///     "build_id (int64)": "new_name",
    ///     ...
    /// }
    /// Ordered by starting_build, covering changes in names of maps between builds
    /// (ie map 269 being known as "Caverns of Time" in classic and "Opening of the Dark Portal" in TBC+)
    /// </summary>
    public string name_history { get; set; }

    /// <summary>
    /// Virtual generated column from coalesce(json->CosmeticParentMapID, json->ParentMapID, null)
    /// </summary>
    public int? parent { get; set; }
}

/// <summary>
/// Which maps are available in which builds
/// Primary key: (build_id, map_id)
/// </summary>
internal class BuildMap
{
    public BuildVersion build_id { get; set; }
    public int map_id { get; set; }
}

/// <summary>
/// List of known TACT decryption keys
/// When we discover new keys, we query for BuildScans in the PartialDecrypt state, and have content that references this key, 
/// queueing for rescan.
/// </summary>
internal class TACTKey
{
    /// <summary>
    /// hex representation of the 8 byte key identifier
    /// stored as uppercase hex string
    /// </summary>
    public string key_name { get; set; }
    public ArraySegment<byte> key { get; set; }
    public Instant discovered { get; set; }
}

/// <summary>
/// Key/value groups of misc settings
/// </summary>
internal class Setting
{
    public string Key { get; set; }
    public string Value { get; set; }
}

// todo

// internal class Minimap
// {
//     /// <summary>
//     /// MD5 hash of the tiles_json - represents this specific tile arrangement
//     /// </summary>
//     public string hash { get; set; } // varchar(32)
//     public int map_id { get; set; }
//     /// <summary>
//     /// JSONB: {"0,5": "hash", "12,34": "hash"}
//     /// </summary>
//     public string tiles_json { get; set; }
// 
//     // primary key: (hash,map_id)
// }
// 
// internal class BuildMinimap
// {
//     public BuildVersion build_id { get; set; }
//     public int map_id { get; set; }
//     public string minimap_hash { get; set; }
// 
//     // Primary key: (build_id, map_id)
//     // mst builds will reuse existing minimap_hash values
// }



#pragma warning restore IDE1006, CS8618

