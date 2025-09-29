using Minimaps.Shared;
using NodaTime;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// Should keep in sync with the scan_state snake-case DB version 
/// </summary>
public enum ScanState
{
    Pending,
    /// <summary>
    /// Scanning failed with an exception, exception column should have error
    /// </summary>
    Exception,
    /// <summary>
    /// We couldn't even access the build, it's protected by an armadillo key (named in encrypted_key)
    /// </summary>
    EncryptedBuild,
    /// <summary>
    /// We could access the build but the map database itself was encrypted (TACT key name hex stored in encrypted_key)
    /// </summary>
    EncryptedMapDatabase,
    /// <summary>
    /// There were some encrypted maps, the map IDs and associated missing key names are stored inside encrypted_maps.
    /// If we discover these keys at a later point we can re-scan for a more complete build (assuming CDN still has the data).
    /// </summary>
    PartialDecrypt,
    /// <summary>
    /// All maps referenced in the database were decrypted successfully
    /// </summary>
    FullDecrypt
}

/// <summary>
/// Each new build (not historical builds) will have an associated scan entry
/// </summary>
internal class BuildScan
{
    public BuildVersion build_id { get; set; }
    public ScanState state { get; set; }
    public Instant first_seen { get; set; }
    public Instant last_scanned { get; set; }
    public Period scan_time { get; set; }

    /// <summary>
    /// Scanner exception when in the Exception state
    /// </summary>
    public string exception { get; set; }

    /// <summary>
    /// Key needed to process this build if we're in the EncryptedBuild state
    /// </summary>
    public string encrypted_key { get; set; }

    /// <summary>
    /// JSONB serialized list of encrypted maps and their decryption key name
    /// IF we're in the PartialDecrypt state
    /// {
    ///     map_id: "key_name",
    ///     ...
    /// }
    /// </summary>
    public string encrypted_maps { get; set; }

    /// <summary>
    /// The specific build/CDN/product used during this scan (FK to BuildProduct)
    /// </summary>
    public string config_build { get; set; }
    public string config_cdn { get; set; }
    public string config_product { get; set; }
}

#pragma warning restore IDE1006, CS8618

