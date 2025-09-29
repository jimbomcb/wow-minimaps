using NodaTime;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// List of known TACT decryption keys
/// When we discover new keys, we query for BuildScans in the PartialDecrypt state, and have content that references this key, 
/// queueing for rescan.
/// </summary>
internal class TACTKey
{
    /// <summary>
    /// hex representation of the 8 byte key identifier
    /// stored as uppercase hex string
    /// </summary>
    public string key_name { get; set; }
    public ArraySegment<byte> key { get; set; }
    public Instant discovered { get; set; }
}

#pragma warning restore IDE1006, CS8618

