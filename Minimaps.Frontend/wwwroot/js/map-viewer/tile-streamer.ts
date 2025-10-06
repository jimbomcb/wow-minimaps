export interface TileRequest {
    hash: string;
    worldX: number;
    worldY: number;
    lodLevel: number;
    layerId: string;
    priority: number;
}

export interface LoadedTile {
    hash: string;
    texture: WebGLTexture;
    worldX: number;
    worldY: number;
    lodLevel: number;
    layerId: string;
}

interface PendingLoad {
    hash: string;
    priority: number;
}

export class TileStreamer {
    private textureCache = new Map<string, WebGLTexture>(); // hash -> texture
    private loadingPromises = new Map<string, Promise<HTMLImageElement>>(); // hash -> loading promise
    private tileLastUsed = new Map<string, number>(); // hash -> timestamp
    private tileLodLevels = new Map<string, number>(); // hash -> lodLevel
    private residentHashes = new Set<string>(); // LOD5+ tiles that never get evicted
    private pendingQueue: PendingLoad[] = []; // Tiles waiting to be loaded
    private maxConcurrentLoads = 6;
    private currentLoads = 0;
    private gl: WebGL2RenderingContext;
    private onTextureLoaded?: () => void;

    constructor(gl: WebGL2RenderingContext) {
        this.gl = gl;
    }

    setTextureLoadedCallback(callback: () => void): void {
        this.onTextureLoaded = callback;
    }

    // per-frame tile request processing
    processFrameRequirements(requests: TileRequest[]): LoadedTile[] {
        for (const request of requests) {

            // todo: think about how I want to handle this...
            if (!this.tileLodLevels.has(request.hash)) {
                this.tileLodLevels.set(request.hash, request.lodLevel);
            }
            this.requestTexture(request.hash, request.priority);
        }

        return this.getAvailableTiles(requests);
    }

    private async requestTexture(hash: string, priority: number): Promise<void> {
        this.tileLastUsed.set(hash, Date.now());

        // Already loaded
        if (this.textureCache.has(hash)) {
            return;
        }

        // Already loading
        if (this.loadingPromises.has(hash)) {
            return;
        }

        // Check if we can start a new load
        if (this.currentLoads >= this.maxConcurrentLoads) {
            const existingIndex = this.pendingQueue.findIndex(p => p.hash === hash);
            if (existingIndex >= 0) {
                const existing = this.pendingQueue[existingIndex];
                if (existing && priority > existing.priority) {
                    // higher prio than before, bump it
                    existing.priority = priority;
                }
            } else {
                this.pendingQueue.push({ hash, priority });
            }
            return;
        }

        // todo: think about how I want to handle tex memory eviction, loading in ALL LOD0 
        // tiles on EK results in ~1GB of video memory, not ideal...
        if (this.textureCache.size > 100) {
            this.evictLRU();
        }

        this.currentLoads++;
        const loadPromise = this.fetchTileByHash(hash);
        this.loadingPromises.set(hash, loadPromise);

        try {
            const image = await loadPromise;
            const lodLevel = this.tileLodLevels.get(hash) ?? 0;
            const texture = this.createTileTexture(image, lodLevel);
            this.textureCache.set(hash, texture);
            this.onTextureLoaded?.();
        } catch (error) {
            console.warn(`Failed to load tile ${hash}:`, error);
        } finally {
            this.loadingPromises.delete(hash);
            this.currentLoads--;
            // Process any queued loads
            this.processQueuedLoads();
        }
    }

    private async fetchTileByHash(hash: string): Promise<HTMLImageElement> {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.crossOrigin = 'anonymous';

            img.onload = () => resolve(img);
            img.onerror = reject;
            img.src = `/data/tile/${hash}`;
        });
    }

    private processQueuedLoads(): void {
        this.pendingQueue.sort((a, b) => b.priority - a.priority);

        while (this.pendingQueue.length > 0 && this.currentLoads < this.maxConcurrentLoads) {
            const pending = this.pendingQueue.shift()!;
            if (this.textureCache.has(pending.hash) || this.loadingPromises.has(pending.hash)) {
                continue;
            }

            this.requestTexture(pending.hash, pending.priority);
        }
    }

    private getAvailableTiles(requests: TileRequest[]): LoadedTile[] {
        const available: LoadedTile[] = [];

        for (const request of requests) {
            const texture = this.textureCache.get(request.hash);
            if (texture) {
                available.push({
                    hash: request.hash,
                    texture,
                    worldX: request.worldX,
                    worldY: request.worldY,
                    lodLevel: request.lodLevel,
                    layerId: request.layerId
                });
            }
        }

        return available;
    }

    markResident(hash: string): void {
        this.residentHashes.add(hash);
    }

    private evictLRU(): void {
        const candidates = Array.from(this.tileLastUsed.entries())
            .filter(([hash]) => !this.residentHashes.has(hash))
            .sort((a, b) => a[1] - b[1]); // Oldest first

        for (const [hash] of candidates.slice(0, 20)) { // 20 oldest
            const texture = this.textureCache.get(hash);
            if (texture) {
                this.gl.deleteTexture(texture);
                this.textureCache.delete(hash);
                this.tileLastUsed.delete(hash);
                this.tileLodLevels.delete(hash);
            }
        }
    }

    getStats() {
        return {
            cachedTextures: this.textureCache.size,
            currentLoads: this.currentLoads,
            residentTiles: this.residentHashes.size
        };
    }

    private createTileTexture(image: HTMLImageElement, lodLevel: number): WebGLTexture {
        const texture = this.gl.createTexture()!;

        this.gl.bindTexture(this.gl.TEXTURE_2D, texture);
        
        // LOD0 tiles are always opaque, LOD1+ tiles may have transparency given holes from missing components
        if (lodLevel === 0) {
            this.gl.texStorage2D(this.gl.TEXTURE_2D, 1, this.gl.RGB8, image.width, image.height);
            this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, 0, 0, this.gl.RGB, this.gl.UNSIGNED_BYTE, image);
        } else {
            this.gl.texStorage2D(this.gl.TEXTURE_2D, 1, this.gl.RGBA8, image.width, image.height);
            this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, 0, 0, this.gl.RGBA, this.gl.UNSIGNED_BYTE, image);
        }

        // LODS already get filtered when they're downscaled, applying a second linear filter looks questionable?
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MIN_FILTER, this.gl.NEAREST);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MAG_FILTER, this.gl.NEAREST);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_S, this.gl.CLAMP_TO_EDGE);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_T, this.gl.CLAMP_TO_EDGE);

        return texture;
    }
}