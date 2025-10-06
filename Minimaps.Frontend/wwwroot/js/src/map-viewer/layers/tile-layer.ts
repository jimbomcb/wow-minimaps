import { Layer } from './layers.js';
import { MinimapComposition, CameraPosition } from '../types.js';
import { TileRequest } from '../tile-streamer.js';

export interface TileLayerOptions {
    id: string;
    mapId: number;
    version: string;
    visible?: boolean;
    opacity?: number;
    zIndex?: number;
    lodLevel?: number;
    //parentLayer?: TileLayer | undefined;
}

interface ViewportBounds {
    minX: number;
    maxX: number;
    minY: number;
    maxY: number;
}

export class TileLayer implements Layer {
    public readonly type = 'tile' as const;
    public readonly id: string;
    public visible: boolean;
    public opacity: number;
    public zIndex: number;
    public readonly mapId: number;
    public readonly version: string;
    public readonly lodLevel: number;
    //public readonly parentLayer: TileLayer | undefined = undefined;

    private composition: MinimapComposition | null = null;
    private loadingPromise: Promise<MinimapComposition> | null = null;
    private loadError: Error | null = null;

    constructor(options: TileLayerOptions) {
        this.id = options.id;
        this.mapId = options.mapId;
        this.version = options.version;
        this.visible = options.visible ?? true;
        this.opacity = options.opacity ?? 1.0;
        this.zIndex = options.zIndex ?? 0;
        this.lodLevel = options.lodLevel ?? 0;
        //this.parentLayer = options.parentLayer;
        this.loadComposition();
    }

    private async loadComposition(): Promise<void> {
        if (this.loadingPromise) return;

        this.loadingPromise = this.fetchComposition();
        try {
            this.composition = await this.loadingPromise;
            this.loadError = null;
        } catch (error) {
            this.loadError = error as Error;
            console.error(`Failed to load composition for layer ${this.id}:`, error);
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

    // Calculate what tiles this layer needs for the current frame
    calculateVisibleTiles(camera: CameraPosition, canvasSize: { width: number, height: number }): TileRequest[] {
        if (!this.visible || !this.composition) return [];

        const optimalLOD = this.calculateOptimalLOD(camera.zoom);
        const bounds = this.calculateViewportBounds(camera, canvasSize);
        const lodData = this.composition.getLODData(optimalLOD);
        if (!lodData) {
            // fall back to the next LOD level's tile data, todo: redoing how we layer the LODs
            for (let fallbackLOD = optimalLOD + 1; fallbackLOD <= 6; fallbackLOD++) {
                const fallbackData = this.composition.getLODData(fallbackLOD);
                if (fallbackData) {
                    return this.generateTileRequests(fallbackData, fallbackLOD, bounds, camera);
                }
            }
            return [];
        }

        return this.generateTileRequests(lodData, optimalLOD, bounds, camera);
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
                    const priority = this.calculateTilePriority(x, y, camera);
                    requests.push({ hash, worldX: x, worldY: y, lodLevel, layerId: this.id, priority });
                }
            }
        }

        return requests;
    }

    // Calculate optimal LOD based on screen-space size of tiles
    private calculateOptimalLOD(zoom: number): number {
        // Built around the fact that we're dealing with a 64x64 grid, and we render the tiles around a 512x512 tilesize
        // With zoom = 1.0, displayed tile size is 1:1 texels to pixels, so LOD0 is optimal
        // 2.0 = 2:1 LOD0 texels per pixel so use LOD1 at 2x2 (2^1 = 2)
        // 4.0 = 4:1 LOD0 texels per pixel so use LOD2 at 4x4 (2^2 = 4)
        // Up to LOD6 where it covers 2^6 = 64 tiles, the 1x1 tile LOD covers the whole map
        const baseLOD = Math.max(0, Math.floor(Math.log2(zoom)));
        return Math.max(this.lodLevel, Math.min(6, baseLOD));
    }

    private calculateViewportBounds(camera: CameraPosition, canvasSize: { width: number, height: number }): ViewportBounds {
        const tilesVisibleX = canvasSize.width / (512 / camera.zoom);
        const tilesVisibleY = canvasSize.height / (512 / camera.zoom);
        
        // padding for scrolling
        const padding = Math.max(1, Math.min(3, Math.floor(Math.max(tilesVisibleX, tilesVisibleY) / 10)));
        return {
            minX: Math.floor(camera.centerX - tilesVisibleX / 2) - padding,
            maxX: Math.ceil(camera.centerX + tilesVisibleX / 2) + padding,
            minY: Math.floor(camera.centerY - tilesVisibleY / 2) - padding,
            maxY: Math.ceil(camera.centerY + tilesVisibleY / 2) + padding
        };
    }

    private isTileInBounds(x: number, y: number, lodLevel: number, bounds: ViewportBounds): boolean {
        const tileSize = Math.pow(2, lodLevel);
        const tileMaxX = x + tileSize;
        const tileMaxY = y + tileSize;
        return !(tileMaxX <= bounds.minX || x >= bounds.maxX || tileMaxY <= bounds.minY || y >= bounds.maxY);
    }

    // currently just prio top layers above lower layers, and tiles closer to the middle over edges
    private calculateTilePriority(x: number, y: number, camera: CameraPosition): number {
        const distanceFromCenter = Math.sqrt(Math.pow(x - camera.centerX, 2) + Math.pow(y - camera.centerY, 2));
        const distancePriority = Math.max(0, 1000 - distanceFromCenter * 10);
        const layerPriorityBoost = this.zIndex * 10000;
        return distancePriority + layerPriorityBoost;
    }
}
