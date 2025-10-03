import { CompositionDto } from "./backend-types.js";

export interface TileCoord {
    x: number;
    y: number;
}

export interface MapViewport {
    centerX: number;
    centerY: number;
    altitude: number;
}

export interface MapViewerOptions {
    container: HTMLCanvasElement;
    mapId: number;
    version: string;
}

export class MinimapComposition {
    private _compositionMap: Map<string, string>;
    private _missingTilesSet: Set<string>;

    constructor(private data: CompositionDto) {
        this._compositionMap = new Map<string, string>();
        this._missingTilesSet = new Set<string>();

        for (const [key, value] of Object.entries(data)) {
            if (key === "_m" && Array.isArray(value)) {
                for (const coord of value) {
                    this._missingTilesSet.add(coord);
                }
            } else {
                this._compositionMap.set(key, value);
            }
        }
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
}

