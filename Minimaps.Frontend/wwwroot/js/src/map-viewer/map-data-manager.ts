import { MinimapComposition } from './types.js';
import { BuildVersion } from './build-version.js';
import { LayerType, LAYER_TYPE_COUNT, isCompositionLayer, isBaseLayer } from './backend-types.js';
import type { MapVersionsDto, VersionEntryDto, CompositionDto, MapListDto } from './backend-types.js';

export interface MapInfo {
    mapId: number;
    directory: string;
    name: string;
    nameHistory: Map<BuildVersion, string>;
    first: BuildVersion;
    last: BuildVersion;
    parent: number | null;
    layerMask: number;
    wdtTileCount: number;
}

export interface MapVersionInfo {
    version: BuildVersion;
    layers: (string | null)[]; // hashes indexed by LayerType
    cdnMissing: (string[] | null)[] | undefined;
    products: string[];
}

export interface LayerData {
    hash: string;
    composition: MinimapComposition | null;
    cdnMissing: Set<string> | null;
}

export interface LoadedMapData {
    mapId: number;
    version: BuildVersion;
    requestedVersion: BuildVersion | 'latest';
    layers: (LayerData | null)[];
    activeBaseLayer: LayerType;
    parent: LoadedMapData | null;
    isFallback: boolean;
    fallbackVersion: BuildVersion | null;
}

export class MapDataManager {
    private mapsCache: Map<number, MapInfo> | null = null;
    private mapsLoadingPromise: Promise<void> | null = null;

    private versionCache = new Map<number, Map<string, VersionEntryDto>>(); // mapId -> (encodedVersionString -> version entry)
    private versionLoadingPromises = new Map<number, Promise<void>>();

    private compositionCache = new Map<string, MinimapComposition>(); // comp hash -> composition
    private compositionLoadingPromises = new Map<string, Promise<MinimapComposition>>();

    constructor() {}

    async loadMaps(): Promise<MapInfo[]> {
        if (this.mapsCache)
            return Array.from(this.mapsCache.values());

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
        const response = await fetch('/data/maps');
        if (!response.ok)
            throw new Error(`Failed to load maps: ${response.statusText}`);

        const data = (await response.json()) as MapListDto;
        this.mapsCache = new Map();

        for (const map of data.maps) {
            const nameHistoryMap = new Map<BuildVersion, string>();
            for (const [encodedStr, name] of Object.entries(map.nameHistory)) {
                nameHistoryMap.set(BuildVersion.parseEncodedString(encodedStr), name);
            }

            this.mapsCache.set(map.mapId, {
                mapId: map.mapId,
                directory: map.directory,
                name: map.name,
                nameHistory: nameHistoryMap,
                first: BuildVersion.parseEncodedString(map.first),
                last: BuildVersion.parseEncodedString(map.last),
                parent: map.parent ?? null,
                layerMask: map.layerMask,
                wdtTileCount: map.wdtTileCount,
            });
        }

        console.log(`Loaded ${this.mapsCache.size} maps from backend`);
    }

    getMap(mapId: number): MapInfo | null {
        return this.mapsCache?.get(mapId) ?? null;
    }

    getAllMaps(): MapInfo[] {
        if (!this.mapsCache) throw new Error('Maps not loaded yet');
        return Array.from(this.mapsCache.values());
    }

    private async loadVersionsForMap(mapId: number): Promise<void> {
        if (this.versionCache.has(mapId)) return;

        if (this.versionLoadingPromises.has(mapId)) {
            await this.versionLoadingPromises.get(mapId);
            return;
        }

        const loadPromise = this.fetchVersionsForMap(mapId);
        this.versionLoadingPromises.set(mapId, loadPromise);
        try { await loadPromise; } finally { this.versionLoadingPromises.delete(mapId); }
    }

    private async fetchVersionsForMap(mapId: number): Promise<void> {
        const response = await fetch(`/data/versions/${mapId}`);
        if (!response.ok) throw new Error(`Failed to load map versions: ${response.statusText}`);

        const data = (await response.json()) as MapVersionsDto;
        const versionMap = new Map<string, VersionEntryDto>();
        for (const [encodedStr, entry] of Object.entries(data.versions)) {
            versionMap.set(encodedStr, entry);
        }

        this.versionCache.set(mapId, versionMap);
        console.log(`Loaded ${versionMap.size} versions for map ${mapId}`);
    }

    async getVersionsForMap(mapId: number): Promise<MapVersionInfo[]> {
        await this.loadVersionsForMap(mapId);
        const entryMap = this.versionCache.get(mapId);
        if (!entryMap) return [];

        const result: MapVersionInfo[] = [];
        for (const [encodedStr, entry] of entryMap.entries()) {
            result.push({
                version: BuildVersion.parseEncodedString(encodedStr),
                layers: entry.l,
                cdnMissing: entry.m,
                products: entry.p,
            });
        }
        return result;
    }

    async loadMapData(mapId: number, version: BuildVersion | 'latest'): Promise<LoadedMapData> {
        if (!this.mapsCache) await this.loadMaps();

        const mapInfo = this.mapsCache!.get(mapId);
        if (!mapInfo) throw new Error(`Map ${mapId} not found`);

        await this.loadVersionsForMap(mapId);
        const versionMap = this.versionCache.get(mapId)!;
        if (versionMap.size === 0) throw new Error(`Map ${mapId} has no available versions`);

        // resolve latest vs sspecific versions
        let resolvedVersion: BuildVersion;
        let versionEntry: VersionEntryDto;
        let isFallback = false;
        let fallbackVersion: BuildVersion | null = null;

        if (version === 'latest') {
            const sorted = Array.from(versionMap.keys()).sort((a, b) => {
                const aBig = BigInt(a);
                const bBig = BigInt(b);
                return aBig < bBig ? 1 : aBig > bBig ? -1 : 0;
            });
            const latestKey = sorted[0]!;
            resolvedVersion = BuildVersion.parseEncodedString(latestKey);
            versionEntry = versionMap.get(latestKey)!;
        } else {
            const entry = versionMap.get(version.encodedValueString);
            if (entry) {
                resolvedVersion = version;
                versionEntry = entry;
            } else {
                const closest = this.findClosestVersion(version, versionMap);
                if (!closest) throw new Error(`No suitable version for map ${mapId} near ${version}`);
                resolvedVersion = closest.version;
                versionEntry = closest.entry;
                isFallback = true;
                fallbackVersion = closest.version;
                console.warn(`Map ${mapId} not available in version ${version}, using closest: ${fallbackVersion}`);
            }
        }

        const layers = await this.loadLayerData(versionEntry); // TODO: Only load relevant composition
        const activeBaseLayer = this.pickBaseLayer(layers);

        const result: LoadedMapData = {
            mapId,
            version: resolvedVersion,
            requestedVersion: version,
            layers,
            activeBaseLayer,
            parent: null,
            isFallback,
            fallbackVersion,
        };

        if (mapInfo.parent !== null) {
            try {
                result.parent = await this.loadMapData(mapInfo.parent, version);
            } catch (error) {
                console.warn(`Failed to load parent map ${mapInfo.parent}:`, error);
            }
        }

        return result;
    }

    private async loadLayerData(entry: VersionEntryDto): Promise<(LayerData | null)[]> {
        // TODO: Got this working but honestly rethinking it, I don't like that
        // we're doing an extra request for a layer we might not use, ideally we
        // ONLY issue the request for the relevant base layer & persist across map nav
        const layers: (LayerData | null)[] = new Array(LAYER_TYPE_COUNT).fill(null);
        const loadPromises: Promise<void>[] = [];

        for (let i = 0; i < LAYER_TYPE_COUNT; i++) {
            const hash = entry.l[i];
            if (!hash) continue;

            const layerType = i as LayerType;
            const cdnMissingHashes = entry.m?.[i];
            const cdnMissing = cdnMissingHashes ? new Set(cdnMissingHashes) : null;

            if (isCompositionLayer(layerType)) {
                loadPromises.push(
                    this.loadComposition(hash).then(composition => {
                        layers[i] = { hash, composition, cdnMissing };
                    })
                );
            } else {
                // Data layers just store the hash, blob fetched on demand
                layers[i] = { hash, composition: null, cdnMissing };
            }
        }

        await Promise.all(loadPromises);
        return layers;
    }

    private pickBaseLayer(layers: (LayerData | null)[]): LayerType {
        if (layers[LayerType.Minimap]) return LayerType.Minimap;
        if (layers[LayerType.MapTexture]) return LayerType.MapTexture;
        return LayerType.Minimap; // shouldn't happen, but default
    }

    private async loadComposition(hash: string): Promise<MinimapComposition> {
        if (this.compositionCache.has(hash))
            return this.compositionCache.get(hash)!;

        if (this.compositionLoadingPromises.has(hash))
            await this.compositionLoadingPromises.get(hash)!;

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
        const response = await fetch(`/data/comp/${hash}`);
        if (!response.ok)
            throw new Error(`Failed to load composition: ${response.statusText}`);
        const data = (await response.json()) as CompositionDto;
        return MinimapComposition.fromData(data);
    }

    async loadBlob<T>(hash: string): Promise<T> {
        const response = await fetch(`/data/blob/${hash}`);
        if (!response.ok)
            throw new Error(`Failed to load blob ${hash}: ${response.statusText}`);
        return (await response.json()) as T;
    }

    clearCache(): void {
        this.mapsCache = null;
        this.mapsLoadingPromise = null;
        this.versionCache.clear();
        this.versionLoadingPromises.clear();
        this.compositionCache.clear();
        this.compositionLoadingPromises.clear();
    }

    private findClosestVersion(
        requestedVersion: BuildVersion,
        versionMap: Map<string, VersionEntryDto>
    ): { version: BuildVersion; entry: VersionEntryDto } | null {
        if (versionMap.size === 0) return null;

        const requestedValue = requestedVersion.encodedValue;
        let bestVersion: BuildVersion | null = null;
        let bestEntry: VersionEntryDto | null = null;
        let bestDistance = Infinity;

        for (const [encodedStr, entry] of versionMap.entries()) {
            const version = BuildVersion.parseEncodedString(encodedStr);
            const diff = requestedValue > version.encodedValue
                ? requestedValue - version.encodedValue
                : version.encodedValue - requestedValue;
            // small penalty when it's a newer than requested, so we prefer the older of two equidistant
            const penalty = version.encodedValue > requestedValue ? 0.1 : 0;
            const distance = Number(diff) + penalty;

            if (distance < bestDistance) {
                bestDistance = distance;
                bestVersion = version;
                bestEntry = entry;
            }
        }

        return bestVersion && bestEntry ? { version: bestVersion, entry: bestEntry } : null;
    }
}
