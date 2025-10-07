import { TileLayer, RenderContext } from './layers.js';
import { MinimapComposition, CameraPosition } from '../types.js';
import { TileRequest } from '../tile-streamer.js';
import { RenderQueue, TileRenderCommand } from '../render-queue.js';
import { TileStreamer } from '../tile-streamer.js';

export interface TileLayerOptions {
    id: string;
    mapId: number;
    version: string;
    tileStreamer: TileStreamer; // Now required
    visible?: boolean;
    opacity?: number;
    zIndex?: number;
    lodLevel?: number;
    residentLodLevel?: number;
    debugSkipLODs?: number[];
}

interface ViewportBounds {
    minX: number;
    maxX: number;
    minY: number;
    maxY: number;
}

export class TileLayerImpl implements TileLayer {
    public readonly type = 'tile' as const;
    public readonly id: string;
    public visible: boolean;
    public opacity: number;
    public zIndex: number;
    public readonly mapId: number;
    public readonly version: string;
    public readonly lodLevel: number;
    public readonly residentLodLevel: number;
    private readonly debugSkipLODs: Set<number>;
    private readonly tileStreamer: TileStreamer;

    public composition: MinimapComposition | null = null;
    private loadingPromise: Promise<MinimapComposition> | null = null;
    private loadError: Error | null = null;

    constructor(options: TileLayerOptions) {
        this.id = options.id;
        this.mapId = options.mapId;
        this.version = options.version;
        this.tileStreamer = options.tileStreamer;
        this.visible = options.visible ?? true;
        this.opacity = options.opacity ?? 1.0;
        this.zIndex = options.zIndex ?? 0;
        this.lodLevel = options.lodLevel ?? 0;
        this.residentLodLevel = options.residentLodLevel ?? 5;
        this.debugSkipLODs = new Set(options.debugSkipLODs ?? []);
        
        this.loadComposition();
    }

    private async loadComposition(): Promise<void> {
        if (this.loadingPromise) return;

        this.loadingPromise = this.fetchComposition();
        try {
            this.composition = await this.loadingPromise;
            this.loadError = null;
            this.markResidentTiles();
        } catch (error) {
            this.loadError = error as Error;
            console.error(`Failed to load composition for layer ${this.id}:`, error);
        }
    }

    private markResidentTiles(): void {
        if (!this.composition) return;

        const residentData = this.composition.getLODData(this.residentLodLevel);
        if (residentData) {
            for (const [hash] of residentData) {
                this.tileStreamer.markResident(hash);
            }
            console.log(`Marked ${residentData.size} resident for layer ${this.id} LOD${this.residentLodLevel}`);
        }
    }

    private async fetchComposition(): Promise<MinimapComposition> {
        // Fetch map versions
        const mapVersReq = await fetch(`/data/versions/${this.mapId}`);
        if (!mapVersReq.ok) {
            throw new Error(`Failed to load map versions: ${mapVersReq.statusText}`);
        }

        const mapVers = await mapVersReq.json() as { versions: Record<string, string> };

        let hash: string;
        if (this.version === 'latest') {
            const vers = Object.values(mapVers.versions);
            if (vers.length === 0) {
                throw new Error(`No versions available for map ${this.mapId}`);
            }
            hash = vers.at(-1)!;
        } else {
            hash = mapVers.versions[this.version] ?? '';
            if (!hash) {
                throw new Error(`Version ${this.version} not found for map ${this.mapId}`);
            }
        }

        // Fetch composition
        const response = await fetch(`/data/comp/${hash}`);
        if (!response.ok) {
            throw new Error(`Failed to load map composition: ${response.statusText}`);
        }

        return MinimapComposition.fromData(await response.json());
    }

    queueRenderCommands(renderQueue: RenderQueue, context: RenderContext): void {
        if (!this.visible || !this.isLoaded()) {
            return;
        }

        // Basic initial approach, just render ALL loaded tiles that are visible, in LOD order
        // Higher LOD tiles render first, then progressively smaller higher resolution tiles render on top
        
        const optimalLOD = this.calculateOptimalLOD(context.camera.zoom, context.lodBias);
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
                    lodLevel: tileRequest.lodLevel
                };
                renderQueue.push(command);
            }
        }
    }

    private calculateVisibleTilesAtLOD(lodLevel: number, bounds: ViewportBounds, camera: CameraPosition): TileRequest[] {
        if (!this.composition) return [];

        const lodData = this.composition.getLODData(lodLevel);
        if (!lodData) return [];
        return this.generateTileRequests(lodData, lodLevel, bounds, camera);
    }

    public isLoaded(): boolean {
        return this.composition !== null;
    }
    
    public isLoading(): boolean {
        return this.loadingPromise !== null && this.composition === null && this.loadError === null;
    }

    public hasError(): boolean {
        return this.loadError !== null;
    }

    public getError(): Error | null {
        return this.loadError;
    }

    public getComposition(): MinimapComposition | null {
        return this.composition;
    }

    public getLoadingPromise(): Promise<MinimapComposition> | null {
        return this.loadingPromise;
    }

    // Calculate what tiles this layer needs for the current frame (kept for compatibility)
    calculateVisibleTiles(camera: CameraPosition, canvasSize: { width: number, height: number }, lodBias?: number): TileRequest[] {
        if (!this.visible || !this.composition) return [];

        const optimalLOD = this.calculateOptimalLOD(camera.zoom, lodBias);
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
                    // todo: getting unwieldy with too many sources, think about how to better handle
                    // goal is to assign prio so that we load in the most important tiles first
                    // So the top level tiles, in the middle of the screen ideally...
                    // But we do prio the resident layer over all else, to minimise the black frames loading at the start
                    const distanceFromCenter = Math.sqrt(Math.pow(x - camera.centerX, 2) + Math.pow(y - camera.centerY, 2));
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
                        priority
                    });
                }
            }
        }

        return requests;
    }

    private calculateOptimalLOD(zoom: number, lodBias: number = 1.0): number {
        // Built around the fact that we're dealing with a 64x64 grid, and we render the tiles around a 512x512 tilesize
        // With zoom = 1.0, displayed tile size is 1:1 texels to pixels, so LOD0 is optimal
        // 2.0 = 2:1 LOD0 texels per pixel so use LOD1 at 2x2 (2^1 = 2)
        // 4.0 = 4:1 LOD0 texels per pixel so use LOD2 at 4x4 (2^2 = 4)
        // Up to LOD6 where it covers 2^6 = 64 tiles, the 1x1 tile LOD covers the whole map
        const biasedZoom = zoom / lodBias;
        const baseLOD = Math.max(0, Math.floor(Math.log2(biasedZoom)));
        return Math.max(this.lodLevel, Math.min(6, baseLOD));
    }

    private calculateViewportBounds(camera: CameraPosition, canvasSize: { width: number, height: number }): ViewportBounds {
        const tilesVisibleX = canvasSize.width / (512 / camera.zoom);
        const tilesVisibleY = canvasSize.height / (512 / camera.zoom);
        
        return {
            minX: Math.floor(camera.centerX - tilesVisibleX / 2),
            maxX: Math.ceil(camera.centerX + tilesVisibleX / 2),
            minY: Math.floor(camera.centerY - tilesVisibleY / 2),
            maxY: Math.ceil(camera.centerY + tilesVisibleY / 2)
        };
    }

    private isTileInBounds(x: number, y: number, lodLevel: number, bounds: ViewportBounds): boolean {
        const tileSize = Math.pow(2, lodLevel);
        const tileMaxX = x + tileSize;
        const tileMaxY = y + tileSize;
        return !(tileMaxX <= bounds.minX || x >= bounds.maxX || tileMaxY <= bounds.minY || y >= bounds.maxY);
    }
}
