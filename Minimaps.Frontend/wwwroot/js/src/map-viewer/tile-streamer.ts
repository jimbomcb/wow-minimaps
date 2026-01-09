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
    lodLevel: number;
}

interface TextureInfo {
    texture: WebGLTexture;
    width: number;
    height: number;
    format: number; // format e.g. gl.RGBA8, gl.RGB8 (LOD0 doesn't need alpha channel)
    memoryBytes: number;
}

export class TileStreamer {
    private textureCache = new Map<string, TextureInfo>(); // hash -> texture info
    private loadingPromises = new Map<string, Promise<ImageBitmap>>(); // hash -> loading promise
    private tileLastUsed = new Map<string, number>(); // hash -> timestamp
    private residentHashes = new Set<string>(); // LOD5+ tiles that never get evicted
    private pendingQueue: PendingLoad[] = []; // Tiles waiting to be loaded
    private maxConcurrentLoads = 12;
    private currentLoads = 0;
    private gl: WebGL2RenderingContext;
    private onTextureLoaded?: () => void;
    private totalGpuBytes: number = 0;
    private tileBaseUrl: string;

    // todo: scale based on canvas size
    private gpuMemoryBudget: number = 200 * 1024 * 1024;

    constructor(gl: WebGL2RenderingContext, tileBaseUrl: string) {
        this.gl = gl;
        this.tileBaseUrl = tileBaseUrl;
        if (!this.tileBaseUrl.endsWith('/')) {
            this.tileBaseUrl += '/';
        }
    }

    setTextureLoadedCallback(callback: () => void): void {
        this.onTextureLoaded = callback;
    }

    getLoadedTile(hash: string): { texture: WebGLTexture } | null {
        const info = this.textureCache.get(hash);
        return info ? { texture: info.texture } : null;
    }

    isLoaded(hash: string): boolean {
        return this.textureCache.has(hash);
    }

    isLoading(hash: string): boolean {
        return this.loadingPromises.has(hash);
    }

    // Mark a tile hash as resident (never evicted)
    markResident(hash: string, lodLevel: number): void {
        this.residentHashes.add(hash);
        if (!this.isLoaded(hash) && !this.isLoading(hash)) {
            this.requestTexture(hash, 999999, lodLevel); // Very high priority for resident tiles
        }
    }

    // Unmark, can be evicted again
    unmarkResident(hash: string): void {
        this.residentHashes.delete(hash);
    }

    // per-frame tile request processing
    processFrameRequirements(requests: TileRequest[]): void {
        for (const request of requests) {
            this.requestTexture(request.hash, request.priority, request.lodLevel);
        }
    }

    private async requestTexture(hash: string, priority: number, lodLevel: number): Promise<void> {
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
            const existingIndex = this.pendingQueue.findIndex((p) => p.hash === hash);
            if (existingIndex >= 0) {
                const existing = this.pendingQueue[existingIndex];
                if (existing && priority > existing.priority) {
                    // higher prio than before, bump it
                    existing.priority = priority;
                }
            } else {
                this.pendingQueue.push({ hash, priority, lodLevel });
            }
            return;
        }

        if (this.totalGpuBytes > this.gpuMemoryBudget) {
            // Trigger eviction to 90%
            this.evictToBudget(this.gpuMemoryBudget * 0.9);
        }

        this.currentLoads++;
        const loadPromise = this.fetchTileByHash(hash);
        this.loadingPromises.set(hash, loadPromise);

        try {
            const imageBitmap = await loadPromise;
            const textureInfo = this.createTileTexture(imageBitmap, lodLevel);
            this.textureCache.set(hash, textureInfo);
            this.totalGpuBytes += textureInfo.memoryBytes;

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
        const response = await fetch(`${this.tileBaseUrl}${hash}`);
        if (!response.ok) {
            throw new Error(`Failed to fetch tile ${hash}: ${response.statusText}`);
        }

        try {
            const data = await response.blob();
            const imageBitmap = await createImageBitmap(data, {
                imageOrientation: 'none',
                premultiplyAlpha: 'none',
                colorSpaceConversion: 'none',
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

            this.requestTexture(pending.hash, pending.priority, pending.lodLevel);
        }
    }

    private evictToBudget(targetMemory: number): void {
        if (this.totalGpuBytes <= targetMemory) {
            return;
        }

        // Get all non-resident tiles sorted by last used time (oldest first)
        const candidates = Array.from(this.tileLastUsed.entries())
            .filter(([hash]) => !this.residentHashes.has(hash))
            .sort((a, b) => a[1] - b[1]);

        let bytesFreed = 0;
        let tilesEvicted = 0;

        for (const [hash] of candidates) {
            if (this.totalGpuBytes - bytesFreed <= targetMemory) {
                break;
            }

            const info = this.textureCache.get(hash);
            if (info) {
                this.gl.deleteTexture(info.texture);
                this.totalGpuBytes -= info.memoryBytes;
                bytesFreed += info.memoryBytes;
                this.textureCache.delete(hash);
                this.tileLastUsed.delete(hash);
                tilesEvicted++;
            }
        }
    }

    private calcTexMemory(width: number, height: number, format: number): number {
        const pixelCount = width * height;
        let bytesPerPixel = 4;
        switch (format) {
            case this.gl.RGBA8:
                bytesPerPixel = 4;
                break;
            case this.gl.RGB8:
                bytesPerPixel = 3;
                break;
            default:
                console.warn(`Unknown texture format ${format}, assuming 4bpp`);
        }

        return pixelCount * bytesPerPixel;
    }

    getStats() {
        return {
            cachedTextures: this.textureCache.size,
            currentLoads: this.currentLoads,
            residentTiles: this.residentHashes.size,
            pendingQueue: this.pendingQueue.length,
            gpuMemoryBytes: this.totalGpuBytes,
            gpuMemoryMB: (this.totalGpuBytes / (1024 * 1024)).toFixed(2),
            gpuMemoryBudgetMB: (this.gpuMemoryBudget / (1024 * 1024)).toFixed(0),
            gpuMemoryUsagePercent: ((this.totalGpuBytes / this.gpuMemoryBudget) * 100).toFixed(1),
        };
    }

    private createTileTexture(imageBitmap: ImageBitmap, lodLevel: number): TextureInfo {
        const texture = this.gl.createTexture()!;
        this.gl.bindTexture(this.gl.TEXTURE_2D, texture);

        // LOD0 tiles are always opaque, LOD1+ tiles may have transparency given holes from missing components
        let format: number;
        if (lodLevel === 0) {
            format = this.gl.RGB8;
            this.gl.texStorage2D(this.gl.TEXTURE_2D, 1, format, imageBitmap.width, imageBitmap.height);
            this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, 0, 0, this.gl.RGB, this.gl.UNSIGNED_BYTE, imageBitmap);
        } else {
            format = this.gl.RGBA8;
            this.gl.texStorage2D(this.gl.TEXTURE_2D, 1, format, imageBitmap.width, imageBitmap.height);
            this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, 0, 0, this.gl.RGBA, this.gl.UNSIGNED_BYTE, imageBitmap);
        }

        // Intentionally LINEAR for min/NEAREST for mag, this is good at least at LOD0 as it's important to retain the
        // nearest-neighbour for seeing specific pixels, but linear reduces the pixel shimmer when zooming out.
        // Maybe this shouldn't apply to LOD1+?
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MIN_FILTER, this.gl.LINEAR);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MAG_FILTER, this.gl.NEAREST);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_S, this.gl.CLAMP_TO_EDGE);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_T, this.gl.CLAMP_TO_EDGE);

        const memoryBytes = this.calcTexMemory(imageBitmap.width, imageBitmap.height, format);
        return {
            texture,
            width: imageBitmap.width,
            height: imageBitmap.height,
            format,
            memoryBytes,
        };
    }
}
