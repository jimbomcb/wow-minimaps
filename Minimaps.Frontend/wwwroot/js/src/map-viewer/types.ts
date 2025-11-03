import { CompositionDto } from "./backend-types.js";
import { BuildVersion } from "./build-version.js";

export interface CameraPosition {
    centerX: number; // X tile coord, 0-64 // todo: -32 to 32
    centerY: number; // Y tile coord, 0-64
    zoom: number;
}

export interface MapViewerOptions {
    container: HTMLCanvasElement;
    mapId: number;
    version: BuildVersion | 'latest';
    initPosition?: Partial<CameraPosition>;
}

export interface CompositionBounds {
    minX: number;
    maxX: number;
    minY: number;
    maxY: number;
    centerX: number;
    centerY: number;
    width: number;
    height: number;
}

export class MinimapComposition {
    private _compositionMap: Map<string, string>;
    private _missingTilesSet: Set<string>;
    private _bounds: CompositionBounds | null = null;
    private _tileSize: number;

    constructor(private data: CompositionDto) {
        this._compositionMap = new Map<string, string>();
        this._missingTilesSet = new Set<string>();
        this._tileSize = data.tileSize ?? 512;

        if (data.m && Array.isArray(data.m)) {
            for (const coord of data.m) {
                this._missingTilesSet.add(coord);
            }
        }
        // Calculate bounds from all tiles (including missing tiles)
        this._bounds = this.calculateBounds();

        // todo: build cache map
    }

    static fromData(data: CompositionDto): MinimapComposition {
        return new MinimapComposition(data);
    }

    get totalTiles(): number {
        return this._compositionMap.size + this._missingTilesSet.size;
    }

    get composition(): ReadonlyMap<string, string> {
        return this._compositionMap;
    }

    get missingTiles(): ReadonlySet<string> {
        return this._missingTilesSet;
    }

    get bounds(): CompositionBounds | null {
        return this._bounds;
    }

    get tileSize(): number {
        return this._tileSize;
    }

    private calculateBounds(): CompositionBounds | null {
        let minX = Number.POSITIVE_INFINITY;
        let maxX = Number.NEGATIVE_INFINITY;
        let minY = Number.POSITIVE_INFINITY;
        let maxY = Number.NEGATIVE_INFINITY;
        let foundAny = false;

        const lod0Data = this.data.lod?.['0'];

        if (!lod0Data)
            throw new Error("Composition data missing LOD 0 data for bounds calculation"); // Compositions should always have LOD0...

        for (const coordinates of Object.values(lod0Data)) {
            for (const coord of coordinates) {
                const [x, y] = coord.split(',').map(Number);
                if (x !== undefined && y !== undefined && !isNaN(x) && !isNaN(y)) {
                    // occupies x to x+1, y to y+1
                    minX = Math.min(minX, x);
                    maxX = Math.max(maxX, x + 1);
                    minY = Math.min(minY, y);
                    maxY = Math.max(maxY, y + 1);
                    foundAny = true;
                }
            }
        }

        // Also include missing tiles in bounds
        for (const coord of this._missingTilesSet) {
            const [x, y] = coord.split(',').map(Number);
            if (x !== undefined && y !== undefined && !isNaN(x) && !isNaN(y)) {
                minX = Math.min(minX, x);
                maxX = Math.max(maxX, x + 1);
                minY = Math.min(minY, y);
                maxY = Math.max(maxY, y + 1);
                foundAny = true;
            }
        }

        if (!foundAny) {
            return null;
        }

        const width = maxX - minX;
        const height = maxY - minY;
        const centerX = minX + width / 2;
        const centerY = minY + height / 2;

        return {
            minX,
            maxX,
            minY,
            maxY,
            centerX,
            centerY,
            width,
            height
        };
    }

    // todo... just a dirty copy of data for now, 
    // but composition is immutable data and we can do better
    getLODData(lodLevel: number): ReadonlyMap<string, string[]> | null {
        const lodData = this.data.lod?.[lodLevel.toString()];
        if (!lodData) return null;

        const result = new Map<string, string[]>();
        for (const [hash, coordinates] of Object.entries(lodData)) {
            result.set(hash, [...coordinates]); // copy
        }
        return result;
    }
}
