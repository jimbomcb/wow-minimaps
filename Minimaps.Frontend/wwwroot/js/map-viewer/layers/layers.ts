import { MinimapComposition } from "../types.js";

// Base map layer, most layers will be layers of map tiles but we'll also
// have things like data layers that can have annotation, an ADT chunk layer etc.
export interface Layer {
    readonly type: string;
    id: string;
    visible: boolean;
    opacity: number;
    zIndex: number;

    // todo: blend mode
}

export interface TileLayer extends Layer {
    readonly type: 'tile';
    composition: MinimapComposition;
    lodLevel: number;

    // todo: initially thinking I might want nested layers, but meh
    parentLayer?: TileLayer;
    
    isLoaded(): boolean;
    isLoading(): boolean;
    hasError(): boolean;
    getError(): Error | null;
    getComposition(): MinimapComposition | null;
    getLoadingPromise(): Promise<MinimapComposition> | null;
    calculateVisibleTiles(camera: any, canvasSize: { width: number, height: number }): any[];
}

export function isTileLayer(layer: Layer): layer is TileLayer {
    return layer.type === 'tile';
}