import { MapViewport } from "./types.js";

// Translate coords between WoW's X+ North Y+ West's 533.333 yard chunks 
// and the 0-64 coordinates used in our viewer.
export class CoordinateTranslator {
    private static readonly WOW_MIN = -17066.666666666666;
    private static readonly WOW_MAX = 17066.666666666666;
    private static readonly WOW_SIZE = 34133.333333333333;
    private static readonly TILE_COUNT = 64;
    private static readonly TILE_SIZE = CoordinateTranslator.WOW_SIZE / CoordinateTranslator.TILE_COUNT;

    private static readonly URL_PRECISION = 6;
    private static readonly ZOOM_PRECISION = 4;

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

    static parseViewportFromUrl(urlParams: URLSearchParams): MapViewport | undefined {
        const x = urlParams.get('x');
        const y = urlParams.get('y');
        const zoom = urlParams.get('zoom');
        
        if (x !== null && y !== null && zoom !== null) {
            const wowX = parseFloat(x);
            const wowY = parseFloat(y);
            const altitude = parseFloat(zoom);
            const internal = CoordinateTranslator.wowToInternal(wowX, wowY);
            return {
                centerX: internal.x,
                centerY: internal.y,
                altitude: altitude
            };
        }
        return undefined;
    }

    static viewportToUrlParams(viewport: MapViewport): string {
        const wow = CoordinateTranslator.internalToWow(viewport.centerX, viewport.centerY);
        const roundedX = Math.round(wow.x * Math.pow(10, CoordinateTranslator.URL_PRECISION)) / Math.pow(10, CoordinateTranslator.URL_PRECISION);
        const roundedY = Math.round(wow.y * Math.pow(10, CoordinateTranslator.URL_PRECISION)) / Math.pow(10, CoordinateTranslator.URL_PRECISION);
        const roundedZoom = Math.round(viewport.altitude * Math.pow(10, CoordinateTranslator.ZOOM_PRECISION)) / Math.pow(10, CoordinateTranslator.ZOOM_PRECISION);
        
        return `x=${roundedX.toFixed(CoordinateTranslator.URL_PRECISION)}&y=${roundedY.toFixed(CoordinateTranslator.URL_PRECISION)}&zoom=${roundedZoom.toFixed(CoordinateTranslator.ZOOM_PRECISION)}`;
    }
}
