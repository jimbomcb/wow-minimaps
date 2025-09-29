using Minimaps.Shared;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

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

#pragma warning restore IDE1006, CS8618

