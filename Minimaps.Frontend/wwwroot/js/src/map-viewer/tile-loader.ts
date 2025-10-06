import { CameraPosition, MinimapComposition, TileCoord } from "./types.js";
import type { MapVersionsDto } from "./backend-types.js";

export class TileLoader {

    static async forVersion(mapId: number, version: string): Promise<TileLoader> {
        const mapVersReq = await fetch(`/data/versions/${mapId}`);
        if (!mapVersReq.ok) {
            throw new Error(`Failed to load map versions: ${mapVersReq.statusText}`);
        }

        const mapVers = await mapVersReq.json() as MapVersionsDto;

        let hash: string;
        if (version === 'latest') {
            const vers = Object.values(mapVers.versions);
            if (vers.length === 0) {
                throw new Error(`No versions available for map ${mapId}`);
            }
            hash = vers.at(-1)!;
        } else {
            hash = mapVers.versions[version] ?? '';
            if (!hash) {
                // todo: bubbling up errors...
                throw new Error(`Version ${version} not found for map ${mapId}`);
            }
        }

        const response = await fetch(`/data/comp/${hash}`);
        if (!response.ok) {
            throw new Error(`Failed to load map composition: ${response.statusText}`);
        }

        const composition = MinimapComposition.fromData(await response.json());
        return new TileLoader(composition);
    }

    private composition: MinimapComposition;
    private cache = new Map<string, HTMLImageElement>();
    private loadingPromises = new Map<string, Promise<HTMLImageElement>>();
    private hashToCoordMap = new Map<string, TileCoord[]>();

    constructor(composition: MinimapComposition) {
        this.composition = composition;
        this.buildHashToCoordMap();
    }

    getComposition(): MinimapComposition {
        return this.composition;
    }

    private buildHashToCoordMap(): void {
        // todo: rework when rethinking composition lookup approach
        for (const [coordString, hash] of this.composition.composition) {
            const [x, y] = coordString.split(',').map(Number) as [number, number];
            if (!this.hashToCoordMap.has(hash)) {
                this.hashToCoordMap.set(hash, []);
            }
            this.hashToCoordMap.get(hash)!.push({ x, y });
        }
    }

    getCoordinatesForHash(hash: string): TileCoord[] {
        // todo: rework when rethinking composition lookup approach
        return this.hashToCoordMap.get(hash) ?? [];
    }

    async loadTileByHash(hash: string): Promise<HTMLImageElement> {
        const cached = this.cache.get(hash);
        if (cached) {
            return cached;
        }

        const loading = this.loadingPromises.get(hash);
        if (loading) {
            return loading;
        }

        const promise = this.fetchTileByHash(hash);
        this.loadingPromises.set(hash, promise);
        try {
            const image = await promise;
            this.cache.set(hash, image);
            return image;
        } catch (error) {
            throw error;
        } finally {
            this.loadingPromises.delete(hash);
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

    getVisibleTilesWithPriority(position: CameraPosition, canvasSize: { width: number, height: number }): Array<{
        hash: string;
        x: number;
        y: number;
        zoom: number;
        priority: number;
    }> {
        const visibleTiles: Array<{ hash: string; x: number; y: number; zoom: number; priority: number; }> = [];
        const bounds = this.calculateViewportBounds(position, canvasSize);
        const processedCoordinates = new Set<string>();
        
        for (const [coordString, hash] of this.composition.composition) {
            const [x, y] = coordString.split(',').map(Number) as [number, number];
            
            if (this.isTileInBounds(x, y, bounds)) {
                const coordKey = `${x},${y}`;
                if (!processedCoordinates.has(coordKey)) {
                    processedCoordinates.add(coordKey);
                    const priority = this.calculateTilePriority(x, y, position, bounds);
                    visibleTiles.push({ hash, x, y, zoom: 0, priority });
                }
            }
        }

        return visibleTiles.sort((a, b) => b.priority - a.priority);
    }

    private calculateViewportBounds(position: CameraPosition, canvasSize: { width: number, height: number }) {
        const tilesVisibleX = canvasSize.width / (512 / position.zoom);
        const tilesVisibleY = canvasSize.height / (512 / position.zoom);
        const padding = Math.max(1, Math.min(3, Math.floor(tilesVisibleX / 10)));
        const bounds = {
            minX: Math.floor(position.centerX - tilesVisibleX / 2) - padding,
            maxX: Math.ceil(position.centerX + tilesVisibleX / 2) + padding,
            minY: Math.floor(position.centerY - tilesVisibleY / 2) - padding,
            maxY: Math.ceil(position.centerY + tilesVisibleY / 2) + padding
        };
        return bounds;
    }

    private isTileInBounds(x: number, y: number, bounds: any): boolean {
        return x >= bounds.minX && x <= bounds.maxX && 
               y >= bounds.minY && y <= bounds.maxY;
    }

    private calculateTilePriority(x: number, y: number, position: CameraPosition, bounds: any): number {
        // todo: what other factors... 

        const distanceFromCenter = Math.sqrt(
            Math.pow(x - position.centerX, 2) + 
            Math.pow(y - position.centerY, 2)
        );
        
        const maxDistance = Math.sqrt(
            Math.pow(bounds.maxX - bounds.minX, 2) + 
            Math.pow(bounds.maxY - bounds.minY, 2)
        );
        
        return Math.max(0, maxDistance - distanceFromCenter);
    }
}