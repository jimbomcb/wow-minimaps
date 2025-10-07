import { MinimapComposition } from "../types.js";
import { RenderQueue } from "../render-queue.js";

export interface RenderContext {
    camera: any;
    canvasSize: { width: number, height: number };
    deltaTime: number;
    lodBias: number;
}

// Base map layer, most layers will be layers of map tiles but we'll also
// have things like data layers that can have annotation, an ADT chunk layer etc.
export interface BaseLayer {
    readonly type: string;
    id: string;
    visible: boolean;
    opacity: number;
    zIndex: number;

    queueRenderCommands(renderQueue: RenderQueue, context: RenderContext): void;
}

// owns a composition of map tiles at specific LODs, pushes tile render commands based on desired LODing
export interface TileLayer extends BaseLayer {
    readonly type: 'tile';
    composition: MinimapComposition | null;
    lodLevel: number;
    residentLodLevel: number;

    isLoaded(): boolean;
    isLoading(): boolean;
    hasError(): boolean;
    getError(): Error | null;
    getComposition(): MinimapComposition | null;
    getLoadingPromise(): Promise<MinimapComposition> | null;
    calculateVisibleTiles(camera: any, canvasSize: { width: number, height: number }): any[];
}

export type Layer = TileLayer;

export const isTileLayer = (layer: Layer): layer is TileLayer => layer.type === 'tile';