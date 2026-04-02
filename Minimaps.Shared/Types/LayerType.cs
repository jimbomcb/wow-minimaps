namespace Minimaps.Shared.Types;

/// <summary>
/// Layer types, stored in PG as an ENUM
/// Layers are either "composition" layers, a single object that contains a tilemap of LOD0 tiles, 
/// or a data layer of an arbitrary per-layer datatype (ie AreaId contains a JSON object of referenced AreaTables & chunk AreaId assignment)
/// </summary>
public enum LayerType : short
{
    /// <summary>Composition: Base level minimap textures pulled from MAID</summary>
    Minimap = 0,
    /// <summary>Composition: Base level maptextures, the baked down terrain texture blends used since ~BFA</summary>
    MapTexture = 1,
    /// <summary>Composition: Specific minimap tiles for underwater, WoW.exe has hardcoded coordinate based 
    /// overrides for zones like Vashj'ir showing these instead of Minimap</summary>
    NoLiquid = 2,
    /// <summary>
    /// Data: JSON data that maps per-tile impass flag data.
    /// Format: {"tiles":{"x,y":"base64_encoded_32_bytes"}}
    /// The 32 base64 bytes are 256 encoded bits, each bit representing that chunk impass flag.
    /// Impass-less tiles are excluded.
    /// </summary>
    Impass = 3,
    /// <summary>
    /// Data: JSON data of per-chunk 4 byte uint AreaIDs, plus the DBC data for referenced (and parents of referenced) AreaIDs.
    /// Format:     
    /// {
    ///   "tiles": {
    ///     "30,25": [0, 0, 1234, 1234, 1234, 5678, 5678, ...],
    ///     "30,26": [5678, 5678, 1234, 0, 0, ...]
    ///   },
    ///   "areas": {
    ///     "1234": { "AreaName_lang": "Elwynn Forest", "ParentAreaID": 1429, ... },
    ///     "5678": { "AreaName_lang": "Goldshire", "ParentAreaID": 1234, ... },
    ///     "1429": { "AreaName_lang": "Eastern Kingdoms", "ParentAreaID": 0, ... }
    ///   }
    ///  }
    /// Each tile has 256 uints, tiles with all 0 will be excluded, 0 represents no zone.
    /// It's repetative, but it compresses out very well with Brotli so not at all a concern (95% compression ratio).
    /// </summary>
    AreaId = 4,
}

public static class LayerTypeExtensions
{
    public const int Count = 5;

    /// <summary>Composition layers, hash points to a composition of tile hashes, tiles get pushed to external storage, data lives in the DB.</summary>
    public static bool IsCompositionLayer(this LayerType type) => type is LayerType.Minimap or LayerType.MapTexture or LayerType.NoLiquid;

    /// <summary>Data layers, hash points to a database-stored blob of data.</summary>
    public static bool IsDataLayer(this LayerType type) => type is LayerType.Impass or LayerType.AreaId;

    /// <summary>The specific selectable "base" layers, only one of these are active at the same time, while other layers are overlaid</summary>
    public static bool IsBaseLayer(this LayerType type) => type is LayerType.Minimap or LayerType.MapTexture;

    /// <summary>Bitmask with all base layer types set</summary>
    public const int BaseLayerMask = (1 << (int)LayerType.Minimap) | (1 << (int)LayerType.MapTexture);

    /// <summary>Bitmask with all composition layer types set</summary>
    public const int CompositionLayerMask = (1 << (int)LayerType.Minimap) | (1 << (int)LayerType.MapTexture) | (1 << (int)LayerType.NoLiquid);
}
