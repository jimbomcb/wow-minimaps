using Minimaps.Shared;
using Minimaps.Shared.Types;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// Which maps are available in which builds, with their tile counts and composition hashes
/// Primary key: (build_id, map_id)
/// </summary>
internal class BuildMap
{
    public BuildVersion build_id { get; set; }
    public int map_id { get; set; }
    public short? tiles { get; set; }
    public ContentHash? composition_hash { get; set; }
}

#pragma warning restore IDE1006, CS8618

