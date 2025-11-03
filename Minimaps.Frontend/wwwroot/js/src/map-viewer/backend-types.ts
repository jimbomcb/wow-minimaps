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
    versions: Record<string, string>; // version encoded value (string) -> composition hash
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
}
