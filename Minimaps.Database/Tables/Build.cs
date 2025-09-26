using Minimaps.Shared;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

internal class Build
{
    /// <summary>
    /// Primary key: build version encoded into a sortable int64
    /// Bit-packed to: expansion(12) | major(12) | minor(8) | build(32)
    /// </summary>
    public BuildVersion id { get; set; }

    /// <summary>
    /// Full version string "11.0.7.58046" [expansion].[major].[minor].[build]
    /// </summary>
    public string version { get; set; }

    /// <summary>
    /// Build is locked and complete, all the content we can extract has been extracted
    /// </summary>
    public bool locked { get; set; }

    /// <summary>
    /// JSONB serialized list of encrypted maps and their decryption key name
    /// {
    ///     map_id: "key_name",
    ///     ...
    /// }
    /// </summary>
    public string encrypted_maps { get; set; }
}

/// <summary>
/// Represents product releases for builds (retail, ptr, beta, etc.)
/// Primary key: (build_id, product)
/// </summary>
internal class BuildProduct
{
    /// <summary>
    /// References Build.id
    /// </summary>
    public BuildVersion build_id { get; set; }

    /// <summary>
    /// Product name (retail, ptr, beta, etc.)
    /// </summary>
    public string product { get; set; }

    public DateTime released { get; set; }

    public string config_build { get; set; }
    public string config_cdn { get; set; }
    public string config_product { get; set; }
    /// <summary>
    /// set of string region tags in which this cdn/build/product was seen
    /// </summary>
    public string[] config_regions { get; set; }
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

    /// <summary>
    /// JSONB object consisting of: 
    /// [ 
    ///     { "build_id (int64)": "new_name" },
    ///     ...
    /// ]
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

