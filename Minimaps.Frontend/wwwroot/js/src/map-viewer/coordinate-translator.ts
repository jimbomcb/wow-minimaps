// Translate coords between WoW's X+ North Y+ West's 533.333 yard chunks 
// and the 0-64 coordinates used in our viewer.
export class CoordinateTranslator {
    private static readonly WOW_MIN = -17066.6666666667;
    private static readonly WOW_MAX = 17066.6666666667;
    private static readonly WOW_SIZE = 34133.3333333333;
    private static readonly TILE_COUNT = 64;
    private static readonly TILE_SIZE = CoordinateTranslator.WOW_SIZE / CoordinateTranslator.TILE_COUNT;

    static wowToInternal(wowX: number, wowY: number): { x: number, y: number } {
        // flip the X/Y to WoW's coordinate system, +X north/+Y west
        const normalizedX = (wowX - CoordinateTranslator.WOW_MIN) / CoordinateTranslator.WOW_SIZE;
        const normalizedY = (wowY - CoordinateTranslator.WOW_MIN) / CoordinateTranslator.WOW_SIZE;
        
        return {
            x: (1 - normalizedY) * CoordinateTranslator.TILE_COUNT,
            y: (1 - normalizedX) * CoordinateTranslator.TILE_COUNT
        };
    }

    static internalToWow(internalX: number, internalY: number): { x: number, y: number } {
        // inverse the X/Y flipping described above
        const normalizedX = 1 - (internalY / CoordinateTranslator.TILE_COUNT);
        const normalizedY = 1 - (internalX / CoordinateTranslator.TILE_COUNT);
        
        return {
            x: normalizedX * CoordinateTranslator.WOW_SIZE + CoordinateTranslator.WOW_MIN,
            y: normalizedY * CoordinateTranslator.WOW_SIZE + CoordinateTranslator.WOW_MIN
        };
    }

    static wowDistanceToInternal(wowDistance: number): number {
        return wowDistance / CoordinateTranslator.TILE_SIZE;
    }

    static internalDistanceToWow(internalDistance: number): number {
        return internalDistance * CoordinateTranslator.TILE_SIZE;
    }
}
