/** Layer type enum - must match C# LayerType and Postgres layer_type_enum */
export enum LayerType {
    Minimap = 0,
    MapTexture = 1,
    NoLiquid = 2,
    Impass = 3,
    AreaId = 4,
}

export const LAYER_TYPE_COUNT = 5;

/** Returns true for layers backed by tile map compositions */
export function isCompositionLayer(type: LayerType): boolean {
    return type === LayerType.Minimap || type === LayerType.MapTexture || type === LayerType.NoLiquid;
}

/** Returns true for layers backed by db provided data blobs */
export function isDataLayer(type: LayerType): boolean {
    return type === LayerType.Impass || type === LayerType.AreaId;
}

/** Returns true for layers that can serve as the underlying base layers (one active at once) */
export function isBaseLayer(type: LayerType): boolean {
    return type === LayerType.Minimap || type === LayerType.MapTexture;
}

export interface CompositionDto {
    tiles: {
        [hash: string]: string[]; // hash -> array of "x,y" coordinates
    };
    missing?: string[]; // "x,y" coordinates of build-missing tiles
    tileSize?: number;
}

export interface MapVersionsDto {
    versions: Record<string, VersionEntryDto>; // encoded BuildVersion string -> entry
}

export interface VersionEntryDto {
    /** Layer hashes indexed by LayerType enum value. Null if layer absent. */
    l: (string | null)[];
    /** CDN-missing tile hashes per layer. Omitted if no layers have cdn_missing. */
    m?: (string[] | null)[];
    /** Product names this build was seen on. */
    p: string[];
}

export interface MapListDto {
    maps: MapListEntryDto[];
}

export interface MapListEntryDto {
    mapId: number;
    directory: string;
    name: string;
    nameHistory: Record<string, string>;
    first: string; // encoded BuildVersion string
    last: string;
    parent?: number;
    wdtTileCount: number;
    versionCount: number;
    uniqueCount: number;
}

export interface ImpassDataDto {
    tiles: Record<string, string>; // "x,y" -> base64 encoded 32 bytes (256 bits, one per chunk)
}

export interface AreaTableRow {
    ID: number;
    AreaName_lang: string;
    ParentAreaID: number;
    [key: string]: unknown;
}

export interface AreaIdDataDto {
    tiles: Record<string, number[]>; // "x,y" -> 256 uint32 area IDs
    areas: Record<string, AreaTableRow>;
}
