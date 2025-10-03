import { Renderer } from "./renderer.js";
import { TileLoader } from "./tile-loader.js";

export interface TileState {
    loading: boolean;
    loaded: boolean;
    failed: boolean;
    texture?: WebGLTexture;
    priority: number;
}

export class TileManager {
    // todo: tile indexing on a number or something...
    private tileStates = new Map<string, TileState>();
    private loadQueue = new Map<string, number>();
    private hashToTileKeys = new Map<string, Set<string>>();
    private tileKeyToHash = new Map<string, string>();
    private hashToTexture = new Map<string, WebGLTexture>();
    private maxConcurrentLoads = 6;
    private currentLoads = 0;
    private tileLoader: TileLoader;
    private renderer: Renderer;
    private isDirty = false;
    private onRenderNeeded?: () => void;

    constructor(tileLoader: TileLoader, renderer: Renderer) {
        this.tileLoader = tileLoader;
        this.renderer = renderer;
    }

    setRenderCallback(callback: () => void): void {
        this.onRenderNeeded = callback;
    }

    requestTile(hash: string, x: number, y: number, zoom: number, priority: number): TileState {
        const tileKey = this.getTileKey(x, y, zoom);
        
        if (!this.hashToTileKeys.has(hash)) {
            this.hashToTileKeys.set(hash, new Set());
        }
        this.hashToTileKeys.get(hash)!.add(tileKey);
        this.tileKeyToHash.set(tileKey, hash);
        
        let state = this.tileStates.get(tileKey);
        
        if (!state) {
            state = { loading: false, loaded: false, failed: false, priority };
            this.tileStates.set(tileKey, state);
        }

        // Check if texture already exists for this hash
        const existingTexture = this.hashToTexture.get(hash);
        if (existingTexture && !state.loaded) {
            state.texture = existingTexture;
            state.loaded = true;
            state.loading = false;
            this.markDirty();
        }

        if (priority > state.priority) {
            state.priority = priority;
            if (!state.loading && !state.loaded && !state.failed) {
                this.loadQueue.set(hash, Math.max(this.loadQueue.get(hash) || 0, priority));
            }
        }

        if (!state.loading && !state.loaded && !state.failed && !existingTexture) {
            this.queueTileLoad(hash, priority);
        }

        return state;
    }

    getLoadedTiles(): Array<{ tileKey: string, texture: WebGLTexture, x: number, y: number, zoom: number }> {
        const loadedTiles: Array<{ tileKey: string, texture: WebGLTexture, x: number, y: number, zoom: number }> = [];
        
        for (const [tileKey, state] of this.tileStates) {
            if (state.loaded && state.texture) {
                const [x, y, zoom] = tileKey.split('-').map(Number);
                loadedTiles.push({ tileKey, texture: state.texture, x, y, zoom });
            }
        }
        
        return loadedTiles;
    }

    isDirtyAndClear(): boolean {
        const wasDirty = this.isDirty;
        this.isDirty = false;
        return wasDirty;
    }

    markDirty(): void {
        this.isDirty = true;
        this.onRenderNeeded?.();
    }

    private queueTileLoad(hash: string, priority: number): void {
        const existingPriority = this.loadQueue.get(hash);
        if (!existingPriority || priority > existingPriority) {
            this.loadQueue.set(hash, priority);
        }
        
        this.processLoadQueue();
    }

    private async processLoadQueue(): Promise<void> {
        if (this.currentLoads >= this.maxConcurrentLoads || this.loadQueue.size === 0) {
            return;
        }

        const sortedEntries = Array.from(this.loadQueue.entries())
            .sort((a, b) => b[1] - a[1]);

        const [hash, priority] = sortedEntries[0];
        this.loadQueue.delete(hash);

        // existing texture for this hash? ie water tiles can share a texture
        const existingTexture = this.hashToTexture.get(hash);
        if (existingTexture) {
            const tileKeys = this.hashToTileKeys.get(hash);
            if (tileKeys) {
                for (const tileKey of tileKeys) {
                    const tileState = this.tileStates.get(tileKey);
                    if (tileState && !tileState.loaded) {
                        tileState.texture = existingTexture;
                        tileState.loaded = true;
                        tileState.loading = false;
                    }
                }
                this.markDirty();
            }
            this.processLoadQueue();
            return;
        }

        const tileKeys = this.hashToTileKeys.get(hash);
        if (!tileKeys || tileKeys.size === 0) {
            console.warn(`No tileKeys found for hash ${hash}`);
            this.processLoadQueue();
            return;
        }

        const anyTileKey = tileKeys.values().next().value;
        const state = this.tileStates.get(anyTileKey);
        if (!state || state.loading || state.loaded) {
            this.processLoadQueue();
            return;
        }

        for (const tileKey of tileKeys) {
            const tileState = this.tileStates.get(tileKey);
            if (tileState) {
                tileState.loading = true;
            }
        }
        this.currentLoads++;

        try {
            const image = await this.tileLoader.loadTileByHash(hash);
            const texture = this.renderer.createTileTexture(image);
            this.hashToTexture.set(hash, texture);
            
            for (const tileKey of tileKeys) {
                const tileState = this.tileStates.get(tileKey);
                if (tileState) {
                    tileState.texture = texture;
                    tileState.loaded = true;
                    tileState.loading = false;
                }
            }
            this.markDirty();
        } catch (error) {
            console.warn(`Failed to load tile ${hash}:`, error);

            for (const tileKey of tileKeys) {
                const tileState = this.tileStates.get(tileKey);
                if (tileState) {
                    tileState.failed = true;
                    tileState.loading = false;
                }
            }
        } finally {
            this.currentLoads--;
            this.processLoadQueue();
        }
    }

    private getTileKey(x: number, y: number, zoom: number): string {
        return `${x}-${y}-${zoom}`;
    }
}