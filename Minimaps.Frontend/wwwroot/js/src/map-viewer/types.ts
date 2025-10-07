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
    version: BuildVersion | string;
}

export class MinimapComposition {
    private _compositionMap: Map<string, string>;
    private _missingTilesSet: Set<string>;

    constructor(private data: CompositionDto) {
        this._compositionMap = new Map<string, string>();
        this._missingTilesSet = new Set<string>();

        if (data.m && Array.isArray(data.m)) {
            for (const coord of data.m) {
                this._missingTilesSet.add(coord);
            }
        }

        // todo: build cache map
        //if (data.lod && data.lod["0"]) {
        //    const lod0 = data.lod["0"];
        //    for (const [hash, coordinates] of Object.entries(lod0)) {
        //        // Each hash maps to an array of coordinate strings
        //        for (const coord of coordinates) {
        //            this._compositionMap.set(coord, hash);
        //        }
        //    }
        //}
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