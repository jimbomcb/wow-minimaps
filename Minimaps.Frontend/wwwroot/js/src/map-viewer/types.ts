import { CompositionDto } from './backend-types.js';
import { BuildVersion } from './build-version.js';

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
    tileBaseUrl: string;
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
    private _coordToHash: Map<string, string>;
    private _missingTilesSet: Set<string>;
    private _bounds: CompositionBounds | null = null;
    private _tileSize: number;

    constructor(private data: CompositionDto) {
        this._coordToHash = new Map<string, string>();
        this._missingTilesSet = new Set<string>();
        this._tileSize = data.tileSize ?? 512;

        if (data.m && Array.isArray(data.m)) {
            for (const coord of data.m) {
                this._missingTilesSet.add(coord);
            }
        }

        // Build coord -> hash lookup and calculate bounds
        this._bounds = this.buildCacheAndBounds();
    }

    static fromData(data: CompositionDto): MinimapComposition {
        return new MinimapComposition(data);
    }

    get totalTiles(): number {
        return this._coordToHash.size + this._missingTilesSet.size;
    }

    get coordToHash(): ReadonlyMap<string, string> {
        return this._coordToHash;
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

    private buildCacheAndBounds(): CompositionBounds | null {
        let minX = Number.POSITIVE_INFINITY;
        let maxX = Number.NEGATIVE_INFINITY;
        let minY = Number.POSITIVE_INFINITY;
        let maxY = Number.NEGATIVE_INFINITY;
        let foundAny = false;

        const lod0Data = this.data.lod?.['0'];
        if (!lod0Data) throw new Error('Composition data missing base tile data');

        // Build coord->hash map and calculate bounds in single pass
        for (const [hash, coordinates] of Object.entries(lod0Data)) {
            for (const coord of coordinates) {
                this._coordToHash.set(coord, hash);

                const [x, y] = coord.split(',').map(Number);
                if (x !== undefined && y !== undefined && !isNaN(x) && !isNaN(y)) {
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

        if (!foundAny) return null;

        const width = maxX - minX;
        const height = maxY - minY;
        return {
            minX,
            maxX,
            minY,
            maxY,
            centerX: minX + width / 2,
            centerY: minY + height / 2,
            width,
            height,
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

    // Compare two compositions, return changed tile coords grouped by change type
    static diff(
        oldComp: MinimapComposition,
        newComp: MinimapComposition
    ): {
        added: Set<string>;
        modified: Set<string>;
        removed: Set<string>;
    } {
        const added = new Set<string>();
        const modified = new Set<string>();
        const removed = new Set<string>();

        const oldCoords = oldComp.coordToHash;
        const newCoords = newComp.coordToHash;

        for (const [coord, oldHash] of oldCoords) {
            const newHash = newCoords.get(coord);
            if (newHash === undefined) {
                removed.add(coord);
            } else if (newHash !== oldHash) {
                modified.add(coord);
            }
        }

        for (const coord of newCoords.keys()) {
            if (!oldCoords.has(coord)) {
                added.add(coord);
            }
        }

        return { added, modified, removed };
    }
}
