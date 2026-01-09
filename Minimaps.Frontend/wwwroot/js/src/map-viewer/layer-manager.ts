import type { Layer } from './layers/layers.js';

// The map viewer owns a layer manager of layers,
// layers can be tile layers for map data, or other kinds of data layers for markup.
export class LayerManager {
    private layers = new Map<string, Layer>();
    private renderOrder: string[] = [];

    addLayer(layer: Layer): void {
        this.layers.set(layer.id, layer);
        this.updateRenderOrder();
    }

    removeLayer(layerId: string): void {
        this.layers.delete(layerId);
        this.renderOrder = this.renderOrder.filter((id) => id !== layerId);
    }

    getVisibleLayers(): Layer[] {
        return this.renderOrder.map((id) => this.layers.get(id)).filter((layer) => layer?.visible) as Layer[];
    }

    // Generic layer filtering
    getLayersOfType<T extends Layer>(filterFn: (layer: Layer) => layer is T): T[] {
        return this.getVisibleLayers().filter(filterFn);
    }

    getLayer(layerId: string): Layer | undefined {
        return this.layers.get(layerId);
    }

    // reorder layer map
    private updateRenderOrder(): void {
        this.renderOrder = Array.from(this.layers.values())
            .sort((a, b) => a.zIndex - b.zIndex)
            .map((layer) => layer.id);
    }
}
