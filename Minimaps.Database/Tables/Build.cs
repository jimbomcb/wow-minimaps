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

// todo

internal class MinimapComposition
{
    /// <summary>
    /// Calculated hash of this specific composition, guaranteed to be unique for each specific minimap layout and contents. 
    /// See MinimapComposition.GenerateHash
    /// </summary>
    public string hash { get; set; }

    /// <summary>
    /// JSONB backed MinimapComposition: {"0,5": "hash", "12,34": "hash"}
    /// </summary>
    public MinimapComposition composition { get; set; }
}

internal class BuildMinimap
{
    public BuildVersion build_id { get; set; }
    public int map_id { get; set; }
    public string composition_hash { get; set; }

    // Primary key: (build_id, map_id)
}

#pragma warning restore IDE1006, CS8618

