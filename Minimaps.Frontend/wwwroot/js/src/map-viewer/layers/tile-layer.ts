import type { TileLayer, RenderContext } from './layers.js';
import type { MinimapComposition, CameraPosition } from '../types.js';
import type { TileRequest } from '../tile-streamer.js';
import type { RenderQueue, TileRenderCommand } from '../render-queue.js';
import type { TileStreamer } from '../tile-streamer.js';
import { computeLodHashes, type LodTileEntry } from '../lod-hash.js';

export interface TileLayerOptions {
    id: string;
    composition: MinimapComposition;
    tileStreamer: TileStreamer;
    cdnMissing?: ReadonlySet<string> | null;
    visible?: boolean;
    opacity?: number;
    zIndex?: number;
    lodLevel?: number;
    residentLodLevel?: number;
    debugSkipLODs?: number[];
    monochrome?: boolean;
}

interface ViewportBounds {
    minX: number;
    maxX: number;
    minY: number;
    maxY: number;
}

let _placeholderTextures: { buildMissing: WebGLTexture; cdnMissing: WebGLTexture } | null = null;

function getPlaceholderTextures(gl: WebGL2RenderingContext): { buildMissing: WebGLTexture; cdnMissing: WebGLTexture } {
    if (_placeholderTextures) return _placeholderTextures;

    const size = 16;
    function createPattern(fillFn: (x: number, y: number) => [number, number, number, number]): WebGLTexture {
        const pixels = new Uint8Array(size * size * 4);
        for (let y = 0; y < size; y++) {
            for (let x = 0; x < size; x++) {
                const [r, g, b, a] = fillFn(x, y);
                const i = (y * size + x) * 4;
                pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a;
            }
        }
        const tex = gl.createTexture()!;
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        return tex;
    }

    _placeholderTextures = {
        // crosshatch out tiles that are build missing: Referenced by the WDT, but the tile was not part of the build/is encrypted.
        buildMissing: createPattern((x, y) => {
            const onDiag = (x + y) % 4 === 0 || (x - y + 8) % 4 === 0;
            return onDiag ? [50, 50, 50, 200] : [25, 25, 25, 140];
        }),
        // stripe out tiles that are cdn missing: The file WAS part of the build, but it is old and no longer exists on the CDN.
        // Blizzard purge old build data from the CDN occasionally. 
        cdnMissing: createPattern((x, y) => {
            const stripe = (x + y) % 4 < 2;
            return stripe ? [200, 140, 30, 180] : [80, 60, 15, 100];
        }),
    };
    return _placeholderTextures;
}

export class TileLayerImpl implements TileLayer {
    public readonly type = 'tile' as const;
    public readonly id: string;
    public visible: boolean;
    public opacity: number;
    public zIndex: number;
    public readonly lodLevel: number;
    public readonly residentLodLevel: number;
    public monochrome: boolean;
    private readonly debugSkipLODs: Set<number>;
    private readonly tileStreamer: TileStreamer;
    private residentHashes: string[] = []; // Track hashes we marked as resident
    // Precomputed LOD data: level -> hash -> coords[]
    private readonly lodCache: Map<number, Map<string, string[]>>;
    // Missing tile coords (LOD0) for placeholder rendering
    private readonly buildMissingCoords: ReadonlySet<string>;
    private readonly cdnMissingCoords: ReadonlySet<string>;

    public composition: MinimapComposition;

    constructor(options: TileLayerOptions) {
        this.id = options.id;
        this.composition = options.composition;
        this.tileStreamer = options.tileStreamer;
        this.visible = options.visible ?? true;
        this.opacity = options.opacity ?? 1.0;
        this.zIndex = options.zIndex ?? 0;
        this.lodLevel = options.lodLevel ?? 0;
        this.residentLodLevel = options.residentLodLevel ?? 5;
        this.monochrome = options.monochrome ?? false;
        this.debugSkipLODs = new Set(options.debugSkipLODs ?? []);

        // Build-missing: coords from composition's missing list
        this.buildMissingCoords = options.composition.missingTiles;

        // CDN-missing: find coords whose hash is in the cdnMissing set
        const cdnMissingSet = options.cdnMissing;
        const cdnCoords = new Set<string>();
        if (cdnMissingSet && cdnMissingSet.size > 0) {
            for (const [coord, hash] of options.composition.coordToHash) {
                if (cdnMissingSet.has(hash)) {
                    cdnCoords.add(coord);
                }
            }
        }
        this.cdnMissingCoords = cdnCoords;

        // Precompute LOD1-6 hashes from LOD0 data
        this.lodCache = this.buildLodCache(options.cdnMissing ?? null);
        this.markResidentTiles();
    }

    private buildLodCache(cdnMissing: ReadonlySet<string> | null): Map<number, Map<string, string[]>> {
        const cache = new Map<number, Map<string, string[]>>();

        // LOD0 from composition directly (skip cdn_missing tiles)
        const lod0 = this.composition.getLOD0Data();
        const lod0Map = new Map<string, string[]>();
        for (const [hash, coords] of lod0) {
            if (cdnMissing?.has(hash)) continue;
            lod0Map.set(hash, coords);
        }
        cache.set(0, lod0Map);

        // LOD1-6 computed from LOD0 hashes
        const lodHashes = computeLodHashes(this.composition.coordToHash, cdnMissing);
        for (const [level, tileMap] of lodHashes) {
            const levelData = new Map<string, string[]>();
            for (const [coord, entry] of tileMap) {
                const existing = levelData.get(entry.hash);
                if (existing) {
                    existing.push(coord);
                } else {
                    levelData.set(entry.hash, [coord]);
                }
            }
            cache.set(level, levelData);
        }

        return cache;
    }

    private markResidentTiles(): void {
        const residentData = this.lodCache.get(this.residentLodLevel);
        if (residentData) {
            for (const [hash] of residentData) {
                this.tileStreamer.markResident(hash, this.residentLodLevel);
                this.residentHashes.push(hash);
            }
        }
    }

    /**
     * Dispose of this layer and clean up resources
     */
    dispose(): void {
        // Unmark all resident tiles we marked
        for (const hash of this.residentHashes) {
            this.tileStreamer.unmarkResident(hash);
        }
        //console.log(`Unmarked ${this.residentHashes.length} resident tiles for layer ${this.id}`);
        this.residentHashes = [];
    }

    queueRenderCommands(renderQueue: RenderQueue, context: RenderContext): void {
        if (!this.visible) {
            return;
        }

        // Basic initial approach, just render ALL loaded tiles that are visible, in LOD order
        // Higher LOD tiles render first, then progressively smaller higher resolution tiles render on top

        const optimalLOD = this.calculateOptimalLOD(context.camera.zoom, this.composition.tileSize, context.lodBias);
        const bounds = this.calculateViewportBounds(context.camera, context.canvasSize);
        const allTileRequests: TileRequest[] = [];

        // we no longer load the LOD levels beyond the resident LOD level, so clamp
        const effectiveOptimalLOD = Math.min(optimalLOD, this.residentLodLevel);

        for (let lodLevel = this.residentLodLevel; lodLevel >= effectiveOptimalLOD; lodLevel--) {
            // temp debug skipping
            if (this.debugSkipLODs.has(lodLevel)) {
                continue;
            }

            const lodRequests = this.calculateVisibleTilesAtLOD(lodLevel, bounds, context.camera);
            allTileRequests.push(...lodRequests);
        }

        // let the streamer do its business given what's being rendered
        if (allTileRequests.length > 0) {
            this.tileStreamer.processFrameRequirements(allTileRequests);
        }

        if (this.buildMissingCoords.size > 0 || this.cdnMissingCoords.size > 0) {
            const placeholders = getPlaceholderTextures(context.gl);
            this.queueMissingPlaceholders(renderQueue, bounds, this.buildMissingCoords, placeholders.buildMissing);
            this.queueMissingPlaceholders(renderQueue, bounds, this.cdnMissingCoords, placeholders.cdnMissing);
        }

        // Now render ALL loaded tiles in the correct order (high LOD to low LOD)
        // This ensures larger tiles are drawn first, then progressively smaller ones on top
        for (const tileRequest of allTileRequests) {
            const loadedTile = this.tileStreamer.getLoadedTile(tileRequest.hash);
            if (loadedTile) {
                const command: TileRenderCommand = {
                    type: 'tile',
                    layerId: this.id,
                    zIndex: this.zIndex,
                    opacity: this.opacity,
                    texture: loadedTile.texture,
                    worldX: tileRequest.worldX,
                    worldY: tileRequest.worldY,
                    lodLevel: tileRequest.lodLevel,
                    monochrome: this.monochrome,
                };
                renderQueue.push(command);
            }
        }
    }

    private queueMissingPlaceholders(
        renderQueue: RenderQueue,
        bounds: ViewportBounds,
        coords: ReadonlySet<string>,
        texture: WebGLTexture
    ): void {
        for (const coord of coords) {
            const [x, y] = coord.split(',').map(Number) as [number, number];
            if (x !== undefined && y !== undefined && this.isTileInBounds(x, y, 0, bounds)) {
                renderQueue.push({
                    type: 'tile',
                    layerId: this.id,
                    zIndex: this.zIndex,
                    opacity: this.opacity,
                    texture,
                    worldX: x,
                    worldY: y,
                    lodLevel: 0,
                    monochrome: false,
                });
            }
        }
    }

    private calculateVisibleTilesAtLOD(
        lodLevel: number,
        bounds: ViewportBounds,
        camera: CameraPosition
    ): TileRequest[] {
        const lodData = this.lodCache.get(lodLevel);
        if (!lodData) return [];
        return this.generateTileRequests(lodData, lodLevel, bounds, camera);
    }

    public isLoaded(): boolean {
        return true; // Always loaded since composition is provided, todo: cleanup...
    }

    public isLoading(): boolean {
        return false; // Always loaded since composition is provided, todo: cleanup...
    }

    public hasError(): boolean {
        return false; // Always loaded since composition is provided, todo: cleanup...
    }

    public getError(): Error | null {
        return null; // Always loaded since composition is provided, todo: cleanup...
    }

    public getComposition(): MinimapComposition | null {
        return this.composition;
    }

    public getLoadingPromise(): Promise<MinimapComposition> | null {
        return null; // Always loaded since composition is provided, todo: cleanup...
    }

    // Calculate what tiles this layer needs for the current frame (kept for compatibility)
    calculateVisibleTiles(
        camera: CameraPosition,
        canvasSize: { width: number; height: number },
        lodBias?: number
    ): TileRequest[] {
        if (!this.visible || !this.composition) return [];

        const optimalLOD = this.calculateOptimalLOD(camera.zoom, this.composition.tileSize, lodBias);
        const bounds = this.calculateViewportBounds(camera, canvasSize);
        return this.calculateVisibleTilesAtLOD(optimalLOD, bounds, camera);
    }

    private generateTileRequests(
        lodData: ReadonlyMap<string, string[]>,
        lodLevel: number,
        bounds: ViewportBounds,
        camera: CameraPosition
    ): TileRequest[] {
        const requests: TileRequest[] = [];

        for (const [hash, coordinates] of lodData) {
            for (const coord of coordinates) {
                const [x, y] = coord.split(',').map(Number) as [number, number];
                if (x !== undefined && y !== undefined && this.isTileInBounds(x, y, lodLevel, bounds)) {
                    // Prioritise tiles centers closest to camera center
                    const tileCenterX = x + Math.pow(2, lodLevel) / 2;
                    const tileCenterY = y + Math.pow(2, lodLevel) / 2;
                    const distanceFromCenter = Math.sqrt(
                        Math.pow(tileCenterX - camera.centerX, 2) + Math.pow(tileCenterY - camera.centerY, 2)
                    );
                    const distancePriority = Math.max(0, 1000 - distanceFromCenter * 10);
                    const layerPriorityBoost = this.zIndex * 10000;
                    const lodPriority = lodLevel === this.residentLodLevel ? 50000 : (6 - lodLevel) * 1000;
                    const priority = distancePriority + layerPriorityBoost + lodPriority;

                    requests.push({
                        hash,
                        worldX: x,
                        worldY: y,
                        lodLevel,
                        layerId: this.id,
                        priority,
                    });
                }
            }
        }

        return requests;
    }

    private calculateOptimalLOD(zoom: number, tileSize: number, lodBias: number = 1.0): number {
        // Built around the fact that we're dealing with a 64x64 grid, and we render the tiles around a 512x512 tilesize
        // With zoom = 1.0, displayed tile size is 1:1 texels to pixels, so LOD0 is optimal
        // 2.0 = 2:1 LOD0 texels per pixel so use LOD1 at 2x2 (2^1 = 2)
        // 4.0 = 4:1 LOD0 texels per pixel so use LOD2 at 4x4 (2^2 = 4)
        // Up to LOD6 where it covers 2^6 = 64 tiles, the 1x1 tile LOD covers the whole map
        //
        // Adjust for non-512 tile sizes:
        // 256px tiles at zoom=1.0 are effectively showing 0.5:1 texel:pixel ratio
        // So we multiply zoom by (tileSize/512) to get the effective zoom for LOD calculation,
        // this means 256px tiles will select a higher LOD to compensate for lower source resolution
        const sizeMultiplier = tileSize / 512.0;
        const effectiveZoom = zoom * sizeMultiplier;

        const biasedZoom = effectiveZoom / lodBias;
        const baseLOD = Math.max(0, Math.floor(Math.log2(biasedZoom)));
        return Math.max(this.lodLevel, Math.min(6, baseLOD));
    }

    private calculateViewportBounds(
        camera: CameraPosition,
        canvasSize: { width: number; height: number }
    ): ViewportBounds {
        const tilesVisibleX = canvasSize.width / (512 / camera.zoom);
        const tilesVisibleY = canvasSize.height / (512 / camera.zoom);

        return {
            minX: Math.floor(camera.centerX - tilesVisibleX / 2),
            maxX: Math.ceil(camera.centerX + tilesVisibleX / 2),
            minY: Math.floor(camera.centerY - tilesVisibleY / 2),
            maxY: Math.ceil(camera.centerY + tilesVisibleY / 2),
        };
    }

    private isTileInBounds(x: number, y: number, lodLevel: number, bounds: ViewportBounds): boolean {
        const tileSize = Math.pow(2, lodLevel);
        const tileMaxX = x + tileSize;
        const tileMaxY = y + tileSize;
        return !(tileMaxX <= bounds.minX || x >= bounds.maxX || tileMaxY <= bounds.minY || y >= bounds.maxY);
    }
}
