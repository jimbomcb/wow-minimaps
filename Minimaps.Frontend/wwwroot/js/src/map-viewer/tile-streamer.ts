export interface TileRequest {
    hash: string;
    worldX: number;
    worldY: number;
    lodLevel: number;
    layerId: string;
    priority: number;
}

interface PendingLoad {
    hash: string;
    priority: number;
}

export class TileStreamer {
    private textureCache = new Map<string, WebGLTexture>(); // hash -> texture
    private loadingPromises = new Map<string, Promise<ImageBitmap>>(); // hash -> loading promise
    private tileLastUsed = new Map<string, number>(); // hash -> timestamp
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

    getLoadedTile(hash: string): { texture: WebGLTexture } | null {
        const texture = this.textureCache.get(hash);
        return texture ? { texture } : null;
    }

    isLoaded(hash: string): boolean {
        return this.textureCache.has(hash);
    }

    isLoading(hash: string): boolean {
        return this.loadingPromises.has(hash);
    }

    // Mark a tile hash as resident (never evicted)
    markResident(hash: string): void {
        this.residentHashes.add(hash);
        if (!this.isLoaded(hash) && !this.isLoading(hash)) {
            this.requestTexture(hash, 999999); // Very high priority for resident tiles
        }
    }

    // Unmark, can be evicted again
    unmarkResident(hash: string): void {
        this.residentHashes.delete(hash);
    }

    // per-frame tile request processing
    processFrameRequirements(requests: TileRequest[]): void {
        for (const request of requests) {
            this.requestTexture(request.hash, request.priority);
        }
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
        // For now, allow more tiles since resident tiles help with LOD fallback
        if (this.textureCache.size > 250) {
            this.evictLRU();
        }

        this.currentLoads++;
        const loadPromise = this.fetchTileByHash(hash);
        this.loadingPromises.set(hash, loadPromise);

        try {
            const imageBitmap = await loadPromise;
            const texture = this.createTileTexture(imageBitmap);
            this.textureCache.set(hash, texture);
            
            // bitmap passed off to the GPU, can be closed now
            imageBitmap.close();
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

    // Stream in the hash bitmap for texture creation
    private async fetchTileByHash(hash: string): Promise<ImageBitmap> {
        const response = await fetch(`/data/tile/${hash}`);
        if (!response.ok) {
            throw new Error(`Failed to fetch tile ${hash}: ${response.statusText}`);
        }
        
        try {
            const data = await response.blob();
            const imageBitmap = await createImageBitmap(data, {
                imageOrientation: 'none',
                premultiplyAlpha: 'none',
                colorSpaceConversion: 'none'
            });
            return imageBitmap;
        } catch (error) {
            throw new Error(`Failed to create ImageBitmap for tile ${hash}: ${error}`);
        }
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

    private evictLRU(): void {
        const candidates = Array.from(this.tileLastUsed.entries())
            .filter(([hash]) => !this.residentHashes.has(hash))
            .sort((a, b) => a[1] - b[1]); // Oldest first

        const evictCount = Math.min(5, candidates.length);
        for (const [hash] of candidates.slice(0, evictCount)) {
            const texture = this.textureCache.get(hash);
            if (texture) {
                this.gl.deleteTexture(texture);
                this.textureCache.delete(hash);
                this.tileLastUsed.delete(hash);
            }
        }
    }

    getStats() {
        return {
            cachedTextures: this.textureCache.size,
            currentLoads: this.currentLoads,
            residentTiles: this.residentHashes.size,
            pendingQueue: this.pendingQueue.length
        };
    }

    private createTileTexture(imageBitmap: ImageBitmap): WebGLTexture {
        const texture = this.gl.createTexture()!;
        this.gl.bindTexture(this.gl.TEXTURE_2D, texture);

        // todo: texture compression, it's gonna require some extra work given all the device specific stuff going on:
        // https://developer.mozilla.org/en-US/docs/Web/API/WebGL_API/Compressed_texture_formats
        // specifically we need to look up the extension list that might vary per device

        // todo: removed the hash to LOD map from the streamer as it shouldn't care about that...
        // but I need to think more about how to better partition LOD0 RGB vs LOD1+ RGBA

        // LOD0 tiles are always opaque, LOD1+ tiles may have transparency given holes from missing components
        //if (lodLevel === 0) {
        //    this.gl.texStorage2D(this.gl.TEXTURE_2D, 1, this.gl.RGB8, imageBitmap.width, imageBitmap.height);
        //    this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, 0, 0, this.gl.RGB, this.gl.UNSIGNED_BYTE, imageBitmap);
        //} else {
        //    this.gl.texStorage2D(this.gl.TEXTURE_2D, 1, this.gl.RGBA8, imageBitmap.width, imageBitmap.height);
        //    this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, 0, 0, this.gl.RGBA, this.gl.UNSIGNED_BYTE, imageBitmap);
        //}

        this.gl.texStorage2D(this.gl.TEXTURE_2D, 1, this.gl.RGBA8, imageBitmap.width, imageBitmap.height);
        this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, 0, 0, this.gl.RGBA, this.gl.UNSIGNED_BYTE, imageBitmap); 

        // Intentionally LINEAR for min/NEAREST for mag, this is good at least at LOD0 as it's important to retain the
        // nearest-neighbour for seeing specific pixels, but linear reduces the pixel shimmer when zooming out.
        // Maybe this shouldn't apply to LOD1+?
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MIN_FILTER, this.gl.LINEAR);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MAG_FILTER, this.gl.NEAREST);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_S, this.gl.CLAMP_TO_EDGE);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_T, this.gl.CLAMP_TO_EDGE);

        return texture;
    }
}
