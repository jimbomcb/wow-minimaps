using Minimaps.Shared;
using NodaTime;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// Represents product releases for builds (retail, ptr, beta, etc.)
/// Primary key: (build_id, product, config_build, config_cdn, config_product)
/// </summary>
public class BuildProduct
{
    /// <summary>
    /// References Build.id
    /// </summary>
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

    public Instant first_seen { get; set; }
}

#pragma warning restore IDE1006, CS8618

