using Minimaps.Shared;
using NodaTime;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// Represents releases of build "product" versions, a new build pushed to retail, ptr, beta, etc.
/// Primary key: id
/// Unique: (build_id, product, config_build, config_cdn, config_product)
/// </summary>
public class Product
{
    public Int64 id { get; set; }

    public BuildVersion build_id { get; set; }

    /// <summary>
    /// Product name (retail, ptr, beta, etc.)
    /// </summary>
    public string product { get; set; }

    public string config_build { get; set; }
    public string config_cdn { get; set; }
    public string config_product { get; set; }
    /// <summary>
    /// set of string region tags in which this cdn/build/product was seen
    /// </summary>
    public string[] config_regions { get; set; }

    public Instant found { get; set; }
}

#pragma warning restore IDE1006, CS8618

