export interface CompositionDto {
    m?: string[];

    lod: {
        [lodLevel: string]: {
            [hash: string]: string[]; // hash -> array of coordinates
        };
    };
}

export interface MapVersionsDto {
    versions: Record<string, string>; // version encoded value (string) -> composition hash
}