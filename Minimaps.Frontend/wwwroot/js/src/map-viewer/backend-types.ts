export interface CompositionDto {
    m?: string[];
    tileSize?: number;

    lod: {
        [lodLevel: string]: {
            [hash: string]: string[]; // hash -> array of coordinates
        };
    };
}

export interface MapVersionsDto {
    versions: Record<string, MapVersionEntryDto>; // version encoded value (string) -> version entry
}

export interface MapVersionEntryDto {
    compositionHash: string;
    products: string[]; // product names in release order (wow, wow_beta, etc)
}

export interface MapListDto {
    maps: MapListEntryDto[];
}

export interface MapListEntryDto {
    mapId: number;
    directory: string;
    name: string;
    nameHistory: Record<string, string>; // Map of BuildVersion encoded string to map alias
    first: string; // BuildVersion encoded string
    last: string; // BuildVersion encoded string
    parent: number | null;
    tileCount: number;
    versionCount: number;
    uniqueCount: number;
}

export interface MapLayerEntryDto {
    compositionHash: string;
    partial: boolean;
}

export interface MapLayersDto {
    layers: Record<string, Record<string, MapLayerEntryDto>>; // layerType -> encodedVersion -> entry
}

// chunk data from /data/chunks/v1/{mapId}/{buildVersion}
export interface ChunkDataDto {
    impass?: ImpassDataDto;
    areaid?: AreaIdDataDto;
}

export interface ImpassDataDto {
    tiles: Record<string, string>; // "x,y" -> base64 encoded 32 bytes (256 bits, one per chunk)
}

export interface AreaTableRow {
    ID: number;
    AreaName_lang: string;
    ParentAreaID: number;
    [key: string]: unknown; // full DB2 row, additional fields vary by build
}

export interface AreaIdDataDto {
    tiles: Record<string, number[]>; // "x,y" -> 256 uint32 area IDs
    areas: Record<string, AreaTableRow>; // referenced AreaTable rows keyed by area ID
}
