namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006

/// <summary>
/// List of tiles keyed on the MD5 hash of the tile contents
/// Redundant as it stands but might need extra metadata (ie backend store)
/// </summary>
internal class MinimapTile
{
    public required string hash { get; set; }
}

#pragma warning restore IDE1006
