namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006 // Naming Styles - this specifically matches the Postgres column names
internal class Build
{
    /// <summary>
    /// wow/wow_classic/wowt(ptr)/wow_classic_ptr/etc
    /// </summary>
    public string product { get; set; }
    /// <summary>
    /// version string "1.2.3.12345" [expansion].[major].[minor].[build]
    /// </summary>
    public string version { get; set; }
    public int ver_expansion { get; set; }
    public int ver_major { get; set; }
    public int ver_minor { get; set; }
    public int ver_build { get; set; }
    public bool processed { get; set; }
    public DateTime published { get; set; }
}

internal class Map
{
    public int id { get; set; }
    /// <summary>
    /// JSONB string of raw DB2 row
    /// </summary>
    public string db2 { get; set; }
    public string directory { get; set; }
    public string name { get; set; }
    //public int? parent { get; set; } // todo: can just be derived from coalesce(db2->CosmeticParentMapID, db2->ParentMapID) I think
}

/// <summary>
/// Individual tile content - can be shared across multiple minimaps
/// </summary>
internal class MinimapTile
{
    public string hash { get; set; } // varchar(32) - MD5 of tile content
    public DateTime first_seen { get; set; }
    // Physical file: /tiles/{hash}.webp
    // kinda redundant, but in the future we might want to reference tiles on a different file backend
}

/// <summary>
/// The specific combination of tiles that make up a minimap for a map
/// Hash of the tile arrangement - often doesn't change between builds
/// </summary>
internal class Minimap
{
    /// <summary>
    /// MD5 hash of the tiles_json - represents this specific tile arrangement
    /// </summary>
    public string hash { get; set; } // varchar(32)
    public int map_id { get; set; }
    /// <summary>
    /// JSONB: {"0,5": "hash", "12,34": "hash"}
    /// </summary>
    public string tiles_json { get; set; }

    // primary key: (hash,map_id)
}

/// <summary>
/// Links builds to minimaps - most builds reference existing minimap hashes
/// Only stores entries when a build actually uses a minimap
/// </summary>
internal class BuildMinimap
{
    public string build_version { get; set; }
    public int map_id { get; set; }
    public string minimap_hash { get; set; }

    // Primary key: (build_version, map_id)
    // Most builds will reuse existing minimap_hash values
}

#pragma warning restore IDE1006 // Naming Styles

