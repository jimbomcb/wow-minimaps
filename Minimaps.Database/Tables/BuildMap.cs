using Minimaps.Shared;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// Which maps are available in which builds
/// Primary key: (build_id, map_id)
/// </summary>
internal class BuildMap
{
    public BuildVersion build_id { get; set; }
    public int map_id { get; set; }
}

#pragma warning restore IDE1006, CS8618

