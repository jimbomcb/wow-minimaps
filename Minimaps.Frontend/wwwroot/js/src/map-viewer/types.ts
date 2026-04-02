import type { CompositionDto } from './backend-types.js';
import type { BuildVersion } from './build-version.js';

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

        if (data.missing && Array.isArray(data.missing)) {
            for (const coord of data.missing) {
                this._missingTilesSet.add(coord);
            }
        }

        // Build coord -> hash lookup from LOD0 tiles
        const tilesData = data.tiles;
        if (tilesData) {
            for (const [hash, coordinates] of Object.entries(tilesData)) {
                for (const coord of coordinates) {
                    this._coordToHash.set(coord, hash);
                }
            }
        }

        this._bounds = this.calculateBounds();
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

    /** Get LOD0 tile data as hash -> coordinates map TODO: Cleanup */
    getLOD0Data(): ReadonlyMap<string, string[]> {
        const result = new Map<string, string[]>();
        for (const [hash, coordinates] of Object.entries(this.data.tiles)) {
            result.set(hash, [...coordinates]);
        }
        return result;
    }

    private calculateBounds(): CompositionBounds | null {
        let minX = Number.POSITIVE_INFINITY;
        let maxX = Number.NEGATIVE_INFINITY;
        let minY = Number.POSITIVE_INFINITY;
        let maxY = Number.NEGATIVE_INFINITY;
        let foundAny = false;

        const updateBounds = (coord: string) => {
            const [x, y] = coord.split(',').map(Number);
            if (x !== undefined && y !== undefined && !isNaN(x) && !isNaN(y)) {
                minX = Math.min(minX, x);
                maxX = Math.max(maxX, x + 1);
                minY = Math.min(minY, y);
                maxY = Math.max(maxY, y + 1);
                foundAny = true;
            }
        };

        for (const coord of this._coordToHash.keys()) {
            updateBounds(coord);
        }

        for (const coord of this._missingTilesSet) {
            updateBounds(coord);
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
