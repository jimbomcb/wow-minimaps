using NodaTime;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// Specific configurations from the TACT server used to acquire builds.
/// Primary key: id
/// Unique: (product_id, config_build, config_cdn, config_product)
/// </summary>
internal class ProductSource
{
    public Int64 id { get; set; }

    /// <summary>
    /// Foreign key to products table
    /// </summary>
    public Int64 product_id { get; set; }

    /// <summary>
    /// Build configuration hash from TACT
    /// </summary>
    public string config_build { get; set; }

    /// <summary>
    /// CDN configuration hash from TACT
    /// </summary>
    public string config_cdn { get; set; }

    /// <summary>
    /// Product configuration hash from TACT
    /// </summary>
    public string config_product { get; set; }

    /// <summary>
    /// Set of string region tags where this specific configuration was observed
    /// (e.g., a source might only be available in ["us"] while the product is in ["cn","us"] with other sources)
    /// </summary>
    public string[] config_regions { get; set; }

    public Instant first_seen { get; set; }
}

#pragma warning restore IDE1006, CS8618
