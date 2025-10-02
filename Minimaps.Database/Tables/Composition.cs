namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

internal class Composition
{
    /// <summary>
    /// Calculated hash of this specific composition, guaranteed to be unique for each specific minimap layout and contents. 
    /// See MinimapComposition.GenerateHash
    /// Stored as BYTEA
    /// </summary>
    public byte[] hash { get; set; }

    /// <summary>
    /// FK to product table for the source product this composition was generated from
    /// </summary>
    public Int64 product_id { get; set; }

    /// <summary>
    /// JSONB backed MinimapComposition: {"0,5": "hash", "12,34": "hash"}
    /// </summary>
    public Composition composition { get; set; }

    /// <summary>
    /// Sum total tiles in the composition
    /// </summary>
    public int tiles { get; set; }

    /// <summary>
    /// Min/max populated tile coords, null with 0 tiles
    /// JSONB in the format { "x0" : -5, "y0": -4, "x1": 5, "y1": 5 }
    /// </summary>
    public string? extents { get; set; }
}

#pragma warning restore IDE1006, CS8618

