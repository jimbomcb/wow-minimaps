using Minimaps.Shared;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

internal class BuildMinimap
{
    public BuildVersion build_id { get; set; }
    public int map_id { get; set; }
    public string composition_hash { get; set; }

    // Primary key: (build_id, map_id)
}

#pragma warning restore IDE1006, CS8618

