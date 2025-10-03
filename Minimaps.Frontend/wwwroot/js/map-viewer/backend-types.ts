export interface CompositionDto extends Record<string, string> {}

export interface MapVersionsDto {
    versions: Record<string, string>; // version encoded value (string) -> composition hash
}