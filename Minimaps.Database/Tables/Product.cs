using Minimaps.Shared;
using NodaTime;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// Represents a build version (1.2.3.456) for a product name/branch (retail, ptr, beta, etc.)
/// Primary key: id
/// Unique: (build_id, product)
/// The associated product sources are in the product_sources table.
/// </summary>
public class Product
{
    public Int64 id { get; set; }

    public BuildVersion build_id { get; set; }

    /// <summary>
    /// Product name (retail, ptr, beta, etc.)
    /// </summary>
    public string product { get; set; }

    /// <summary>
    /// set of string region tags which this product has sources in
    /// </summary>
    public string[] config_regions { get; set; }

    public Instant first_seen { get; set; }
}

#pragma warning restore IDE1006, CS8618

