import { MinimapComposition } from './types.js';
import { BuildVersion } from './build-version.js';
import { MapVersionsDto, MapVersionEntryDto, CompositionDto, MapListDto } from './backend-types.js';

export interface MapInfo {
    mapId: number;
    directory: string;
    name: string;
    nameHistory: Map<BuildVersion, string>;
    first: BuildVersion;
    last: BuildVersion;
    parent: number | null;
    tileCount: number;
}

export interface MapVersionInfo {
    version: BuildVersion;
    compositionHash: string;
    products: string[];
}

export interface LoadedMapData {
    mapId: number;
    version: BuildVersion;
    requestedVersion: BuildVersion | 'latest';
    composition: MinimapComposition;
    compositionHash: string;
    parentMapId: number | null;
    parentComposition?: MinimapComposition;
    parentCompositionHash?: string;
    isFallback: boolean;
    fallbackVersion: BuildVersion | undefined;
}

export class MapDataManager {
    private mapsCache: Map<number, MapInfo> | null = null;
    private mapsLoadingPromise: Promise<void> | null = null;

    private versionCache = new Map<number, Map<string, MapVersionEntryDto>>(); // mapId -> (encodedVersionString -> version entry)
    private versionLoadingPromises = new Map<number, Promise<void>>();

    private compositionCache = new Map<string, MinimapComposition>(); // comp hash -> composition
    private compositionLoadingPromises = new Map<string, Promise<MinimapComposition>>();

    constructor() { }

    async loadMaps(): Promise<MapInfo[]> {
        if (this.mapsCache) {
            return Array.from(this.mapsCache.values());
        }

        if (this.mapsLoadingPromise) {
            await this.mapsLoadingPromise;
            return Array.from(this.mapsCache!.values());
        }

        this.mapsLoadingPromise = this.fetchMaps();
        await this.mapsLoadingPromise;
        this.mapsLoadingPromise = null;

        return Array.from(this.mapsCache!.values());
    }

    private async fetchMaps(): Promise<void> {
        try {
            const response = await fetch('/data/maps');
            if (!response.ok) {
                throw new Error(`Failed to load maps: ${response.statusText}`);
            }

            const data = await response.json() as MapListDto;
            this.mapsCache = new Map();

            for (const map of data.maps) {
                const nameHistoryMap = new Map<BuildVersion, string>();
                for (const [encodedStr, name] of Object.entries(map.nameHistory)) {
                    const version = new BuildVersion(BigInt(encodedStr));
                    nameHistoryMap.set(version, name);
                }

                const mapInfo: MapInfo = {
                    mapId: map.mapId,
                    directory: map.directory,
                    name: map.name,
                    nameHistory: nameHistoryMap,
                    first: new BuildVersion(BigInt(map.first)),
                    last: new BuildVersion(BigInt(map.last)),
                    parent: map.parent,
                    tileCount: map.tileCount
                };
                this.mapsCache.set(mapInfo.mapId, mapInfo);
            }

            console.log(`Loaded ${this.mapsCache.size} maps from backend`);
        } catch (error) {
            console.error('Failed to load maps:', error);
            throw error;
        }
    }

    getMap(mapId: number): MapInfo | null {
        return this.mapsCache?.get(mapId) ?? null;
    }

    getAllMaps(): MapInfo[] {
        if (!this.mapsCache) {
            throw new Error('Maps not loaded yet');
        }
        return Array.from(this.mapsCache.values());
    }

    /**
     * Load the map composition for a specific map ID and version.
     * Also load the parent map composition if one exists.
     * Return both main and parent compositions.
     * If requested version doesn't exist find the closest available version.
     */
    async loadMapData(mapId: number, version: BuildVersion | 'latest'): Promise<LoadedMapData> {
        if (!this.mapsCache) {
            await this.loadMaps();
        }

        const mapInfo = this.mapsCache!.get(mapId);
        if (!mapInfo) {
            throw new Error(`Map ${mapId} not found`);
        }

        await this.loadVersionsForMap(mapId);
        const versionMap = this.versionCache.get(mapId)!;

        if (versionMap.size === 0) {
            throw new Error(`Map ${mapId} has no available versions in the database`);
        }

        let compositionHash: string;
        let resolvedVersion: BuildVersion;
        let isFallback = false;
        let fallbackVersion: BuildVersion | undefined;

        if (version === 'latest') {
            const sortedKeys = Array.from(versionMap.keys()).sort((a, b) => {
                const aBig = BigInt(a);
                const bBig = BigInt(b);
                return aBig < bBig ? 1 : aBig > bBig ? -1 : 0; // Descending
            });
            const latestKey = sortedKeys[0];
            if (!latestKey) {
                throw new Error(`Failed to determine latest version for map ${mapId}`);
            }

            const latestEntry = versionMap.get(latestKey);
            if (!latestEntry) {
                throw new Error(`Composition hash not found for latest version of map ${mapId}`);
            }

            compositionHash = latestEntry.compositionHash;
            resolvedVersion = new BuildVersion(BigInt(latestKey));
        } else {
            const versionKey = version.encodedValueString;
            const entry = versionMap.get(versionKey);

            if (!entry) {
                // Version doesn't exist for this map, find closest
                console.log(`Map ${mapId} not found in version ${version.toString()}, searching for closest version...`);
                const closest = this.findClosestVersion(version, versionMap);
                if (closest) {
                    compositionHash = closest.hash;
                    resolvedVersion = closest.version;
                    isFallback = true;
                    fallbackVersion = closest.version;
                    console.warn(`Map ${mapId} not available in version ${version.toString()}, using closest: ${fallbackVersion.toString()}`);
                } else {
                    throw new Error(`No suitable version found for map ${mapId} near ${version.toString()}`);
                }
            } else {
                compositionHash = entry.compositionHash;
                resolvedVersion = version;
            }
        }

        const composition = await this.loadComposition(compositionHash);

        const result: LoadedMapData = {
            mapId,
            version: resolvedVersion,
            requestedVersion: version,
            composition,
            compositionHash,
            parentMapId: mapInfo.parent,
            isFallback,
            fallbackVersion
        };

        // If there's a parent map, load its composition too
        if (mapInfo.parent !== null) {
            try {
                const parentData = await this.loadMapData(mapInfo.parent, version);
                result.parentComposition = parentData.composition;
                result.parentCompositionHash = parentData.compositionHash;
                console.log(`Loaded parent map ${mapInfo.parent} for map ${mapId}`);
            } catch (error) {
                console.warn(`Failed to load parent map ${mapInfo.parent} for map ${mapId}:`, error);
            }
        }

        return result;
    }

    private async loadVersionsForMap(mapId: number): Promise<void> {
        if (this.versionCache.has(mapId)) {
            return;
        }

        if (this.versionLoadingPromises.has(mapId)) {
            await this.versionLoadingPromises.get(mapId);
            return;
        }

        const loadPromise = this.fetchVersionsForMap(mapId);
        this.versionLoadingPromises.set(mapId, loadPromise);

        try {
            await loadPromise;
        } finally {
            this.versionLoadingPromises.delete(mapId);
        }
    }

    private async fetchVersionsForMap(mapId: number): Promise<void> {
        try {
            const response = await fetch(`/data/versions/${mapId}`);
            if (!response.ok) {
                throw new Error(`Failed to load map versions: ${response.statusText}`);
            }

            const data = await response.json() as MapVersionsDto;

            const versionMap = new Map<string, MapVersionEntryDto>();
            for (const [encodedStr, entry] of Object.entries(data.versions)) {
                versionMap.set(encodedStr, entry);
            }

            this.versionCache.set(mapId, versionMap);
            console.log(`Loaded ${versionMap.size} versions for map ${mapId}`);
        } catch (error) {
            console.error(`Failed to load versions for map ${mapId}:`, error);
            throw error;
        }
    }

    async getVersionsForMap(mapId: number): Promise<MapVersionInfo[]> {
        await this.loadVersionsForMap(mapId);
        const entryMap = this.versionCache.get(mapId);
        if (!entryMap) {
            return [];
        }

        const result: MapVersionInfo[] = [];
        for (const [encodedStr, entry] of entryMap.entries()) {
            result.push({
                version: new BuildVersion(BigInt(encodedStr)),
                compositionHash: entry.compositionHash,
                products: entry.products
            });
        }
        return result;
    }

    private async loadComposition(hash: string): Promise<MinimapComposition> {
        if (this.compositionCache.has(hash)) {
            return this.compositionCache.get(hash)!;
        }

        if (this.compositionLoadingPromises.has(hash)) {
            return await this.compositionLoadingPromises.get(hash)!;
        }

        const loadPromise = this.fetchComposition(hash);
        this.compositionLoadingPromises.set(hash, loadPromise);

        try {
            const composition = await loadPromise;
            this.compositionCache.set(hash, composition);
            return composition;
        } finally {
            this.compositionLoadingPromises.delete(hash);
        }
    }

    private async fetchComposition(hash: string): Promise<MinimapComposition> {
        try {
            const response = await fetch(`/data/comp/${hash}`);
            if (!response.ok) {
                throw new Error(`Failed to load composition: ${response.statusText}`);
            }

            const data = await response.json() as CompositionDto;
            return MinimapComposition.fromData(data);
        } catch (error) {
            console.error(`Failed to load composition ${hash}:`, error);
            throw error;
        }
    }

    clearCache(): void {
        this.mapsCache = null;
        this.mapsLoadingPromise = null;
        this.versionCache.clear();
        this.versionLoadingPromises.clear();
        this.compositionCache.clear();
        this.compositionLoadingPromises.clear();
    }

    private findClosestVersion(requestedVersion: BuildVersion, versionMap: Map<string, MapVersionEntryDto>): { version: BuildVersion; hash: string } | null {
        if (versionMap.size === 0) return null;

        const requestedValue = requestedVersion.encodedValue;
        const versions = Array.from(versionMap.entries()).map(([encodedStr, entry]) => {
            const version = new BuildVersion(BigInt(encodedStr));
            return {
                version,
                hash: entry.compositionHash,
                distance: this.calculateVersionDistance(requestedValue, version.encodedValue)
            };
        });

        versions.sort((a, b) => a.distance - b.distance);

        const closest = versions[0];
        return closest ? { version: closest.version, hash: closest.hash } : null;
    }

    private calculateVersionDistance(requested: bigint, candidate: bigint): number {
        const diff = requested > candidate ? requested - candidate : candidate - requested;
        // small penalty when it's a newer than requested, so we prefer the older of two equidistant
        const penalty = candidate > requested ? 0.1 : 0;
        return Number(diff) + penalty;
    }
}
