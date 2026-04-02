import type { MapViewerOptions} from './types.js';
import { CameraPosition } from './types.js';
import { Renderer } from './renderer.js';
import { BuildVersion } from './build-version.js';
import { CameraController } from './camera-controller.js';
import { LayerManager } from './layer-manager.js';
import { TileStreamer, TileRequest } from './tile-streamer.js';
import type { TileLayerOptions } from './layers/tile-layer.js';
import { TileLayerImpl } from './layers/tile-layer.js';
import type { TileLayer } from './layers/layers.js';
import { isTileLayer } from './layers/layers.js';
import type { RenderContext } from './layers/layers.js';
import { RenderQueue } from './render-queue.js';
import { CoordinateTranslator } from './coordinate-translator.js';
import { ControlPanel } from './control-panel.js';
import { ImpassLayer } from './layers/impass-layer.js';
import { AreaHighlightLayer } from './layers/area-highlight-layer.js';
import { LayerType, isCompositionLayer, isDataLayer, isBaseLayer } from './backend-types.js';
import type { ImpassDataDto, AreaIdDataDto } from './backend-types.js';
import { MapDataManager } from './map-data-manager.js';
import type { LayerData, LoadedMapData } from './map-data-manager.js';
import { MinimapComposition } from './types.js';
import { DebugPanel } from './debug-panel.js';
import { FlashOverlay } from './flash-overlay.js';

export async function MapViewerInit() {
    const canvas = document.getElementById('map-canvas') as HTMLCanvasElement;
    if (!canvas) {
        console.error('missing #map-canvas');
        return;
    }

    const tileBaseUrl = canvas.dataset['tileBaseUrl'] || '/data/tile/';

    const pathParts = window.location.pathname.split('/').filter((part) => part.length > 0);
    if (pathParts.length < 1 || pathParts[0] !== 'map') {
        console.error('invalid URL format, expected /map/{mapId}/{version}');
        return;
    }

    let mapId = 0;
    if (pathParts.length >= 2) {
        mapId = parseInt(pathParts[1]!, 10);
    }

    // Append /latest if no specified version
    if (pathParts.length === 2) {
        const newUrl = `/map/${mapId}/latest${window.location.hash}`;
        window.history.replaceState({}, '', newUrl);
    }

    // Parse out the version, we only either take a string formatted BuildVersion (1.2.3.456 OR a string with the value "latest")
    let version: BuildVersion | 'latest' = 'latest';
    if (pathParts.length > 2) {
        const versionStr = pathParts[2];
        if (!versionStr) {
            console.error('invalid version in URL');
            return;
        }

        if (versionStr === 'latest') {
            version = 'latest';
        } else {
            const buildVer = BuildVersion.tryParse(versionStr);
            if (buildVer !== null) {
                console.log('Querying exact version ', buildVer.toString());
                version = buildVer;
            } else {
                console.warn(`Invalid version format in URL: ${versionStr}, falling back to 'latest'`);
                version = 'latest';
            }
        }
    }

    // Parse from URL: /map/{mapId}/{version}/{x}/{y}/{zoom}
    const hasCoordinates = pathParts[3] !== undefined && pathParts[4] !== undefined;

    let mapViewerInstance: MapViewer;
    if (hasCoordinates) {
        // Convert from the WoW -16 to 16k into the 0-64 internal positioning
        const wowX = parseFloat(pathParts[3]!);
        const wowY = parseFloat(pathParts[4]!);
        const internal = CoordinateTranslator.wowToInternal(wowX, wowY);
        const zoom = pathParts[5] !== undefined ? parseFloat(pathParts[5]) : 30;

        mapViewerInstance = new MapViewer({
            container: canvas,
            mapId: mapId,
            version: version,
            initPosition: {
                centerX: internal.x,
                centerY: internal.y,
                zoom: zoom,
            },
            tileBaseUrl: tileBaseUrl,
        });
    } else {
        mapViewerInstance = new MapViewer({
            container: canvas,
            mapId: mapId,
            version: version,
            tileBaseUrl: tileBaseUrl,
        });
    }

    return () => {
        mapViewerInstance.dispose();
    };
}

export class MapViewer {
    private canvas: HTMLCanvasElement;
    private renderer: Renderer;
    private cameraController: CameraController;
    private animationId?: number;
    private gl: WebGL2RenderingContext;
    private needsRender = true;
    private layerManager: LayerManager;
    private tileStreamer: TileStreamer;
    private controlPanel: ControlPanel;
    private mapDataManager: MapDataManager;
    private debugPanel: DebugPanel | null = null;

    private renderQueue: RenderQueue;
    private lastFrameTime: number = 0;

    private currentMapId: number;
    private currentVersion: BuildVersion | 'latest'; // The version used internally
    private requestedVersion: BuildVersion | 'latest'; // The version originally requested (for URL display)
    private tileBaseUrl: string;

    private flashOverlay: FlashOverlay;
    private currentComposition: MinimapComposition | null = null;
    private currentMapData: LoadedMapData | null = null;
    private mapLoadGeneration: number = 0;
    private areaHighlightLayer: AreaHighlightLayer | null = null;

    constructor(options: MapViewerOptions) {
        this.canvas = options.container;
        this.currentMapId = options.mapId;
        this.currentVersion = options.version;
        this.requestedVersion = options.version;
        this.tileBaseUrl = options.tileBaseUrl;

        // Track if we have initial position from URL
        const hasInitialPosition =
            options.initPosition !== undefined &&
            options.initPosition.centerX !== undefined &&
            options.initPosition.centerY !== undefined;

        this.gl = this.canvas.getContext('webgl2', { stencil: true })!;
        if (!this.gl) throw new Error('WebGL2 not supported');

        this.renderQueue = new RenderQueue();
        this.renderer = new Renderer(this.canvas);
        this.resizeCanvas();

        this.layerManager = new LayerManager();
        this.tileStreamer = new TileStreamer(this.gl, this.tileBaseUrl);
        this.tileStreamer.setTextureLoadedCallback(() => {
            this.scheduleRender();
        });

        this.mapDataManager = new MapDataManager();
        this.flashOverlay = new FlashOverlay();

        this.cameraController = new CameraController({
            centerX: options.initPosition?.centerX ?? 32,
            centerY: options.initPosition?.centerY ?? 32,
            zoom: options.initPosition?.zoom ?? 30,
        });

        this.cameraController.attachCanvas(this.canvas, () => this.resizeCanvas());
        this.cameraController.onCameraMoved((_) => {
            this.scheduleRender();
        });
        this.cameraController.onCameraReleased((_) => {
            this.updateURL();
        });

        // Initialize control panel
        this.controlPanel = new ControlPanel({
            currentMapId: this.currentMapId,
            currentVersion: this.currentVersion,
            mapDataManager: this.mapDataManager,
            layerManager: this.layerManager,
            onMapChange: (mapId) => this.handleMapChange(mapId),
            onVersionChange: (version) => this.handleVersionChange(version),
            onLayerChange: () => this.scheduleRender(),
            onBaseLayerChange: (lt) => this.setActiveBaseLayer(lt),
            getAvailableBaseLayers: () => this.getAvailableBaseLayers(),
            isLayerCdnIncomplete: (lt) => {
                const layer = this.currentMapData?.layers[lt];
                return layer?.cdnMissing !== null && layer?.cdnMissing !== undefined && layer.cdnMissing.size > 0;
            },
            onZoneHover: (areaId) => this.handleZoneHover(areaId),
            onZoneClick: (areaId) => this.handleZoneClick(areaId),
        });

        const debugContainer = document.getElementById('debug-panel-container');
        if (debugContainer) {
            this.debugPanel = new DebugPanel({
                container: debugContainer,
                enabled: this.canvas.dataset['debugEnabled'] === 'true',
            });
            this.debugPanel.setTileStreamer(this.tileStreamer);
            this.debugPanel.setCameraController(this.cameraController);
        }

        this.startRenderLoop();

        this.loadMap(options.mapId, this.currentVersion, !hasInitialPosition);
    }

    /**
     * Load a map and its compositions, creating the necessary tile layers
     * @param showFlash Whether to show flash effect for changed tiles (only for version changes on same map)
     */
    private async loadMap(
        mapId: number,
        version: BuildVersion | 'latest',
        autoZoom: boolean = false,
        showFlash: boolean = false
    ): Promise<void> {
        const thisGeneration = ++this.mapLoadGeneration;
        this.tileStreamer.clearPendingQueue();

        try {
            const versionStr = version === 'latest' ? 'latest' : version.toString();
            console.log(`Loading map ${mapId} version ${versionStr} (gen ${thisGeneration})...`);

            const oldComposition = showFlash ? this.currentComposition : null;

            const mapData = await this.mapDataManager.loadMapData(mapId, version);

            if (thisGeneration !== this.mapLoadGeneration) {
                console.log(`Map load gen ${thisGeneration} superseded by gen ${this.mapLoadGeneration}, discarding`);
                return;
            }

            // Only update currentVersion if this wasn't a fallback, so that navigating
            // back to a map that has the original version will use it correctly.
            if (!mapData.isFallback) {
                this.currentVersion = mapData.version;
            }

            for (const layer of this.layerManager.getAllLayers()) {
                layer.dispose?.();
                this.layerManager.removeLayer(layer.id);
            }

            // Clear zones and area highlight until chunk data loads for the new map
            this.controlPanel.setZones(null);
            this.areaHighlightLayer = null;

            // Parent map (monochrome background context)
            if (mapData.parent) {
                const parentBase = mapData.parent.layers[mapData.parent.activeBaseLayer];
                if (parentBase?.composition) {
                    this.addTileLayerForComposition(mapData.parent.mapId, parentBase.composition, {
                        id: `parent-${mapData.parent.mapId}`,
                        zIndex: -1,
                        residentLodLevel: 4,
                        monochrome: true,
                        cdnMissing: parentBase.cdnMissing,
                    });
                }
            }

            // Base layers
            this.currentMapData = mapData;
            for (const baseType of [LayerType.Minimap, LayerType.MapTexture]) {
                const baseData = mapData.layers[baseType];
                if (!baseData?.composition) continue;

                const isActive = baseType === mapData.activeBaseLayer;
                this.addTileLayerForComposition(mapId, baseData.composition, {
                    id: `base-${LayerType[baseType]}`,
                    visible: isActive,
                    zIndex: 0,
                    residentLodLevel: 4,
                    cdnMissing: baseData.cdnMissing,
                });

                if (isActive) {
                    this.currentComposition = baseData.composition;

                    if (oldComposition) {
                        // todo: think more about how I want to handle diff flashes
                        const changes = MinimapComposition.diff(oldComposition, baseData.composition);
                        if (changes.added.size + changes.modified.size + changes.removed.size > 0) {
                            this.flashOverlay.clear();
                            this.flashOverlay.triggerFlash(changes);
                        }
                    }

                    if (autoZoom && baseData.composition.bounds) {
                        this.cameraController.fitToBounds(baseData.composition.bounds, 10);
                    }
                }
            }

            // Overlay composition layers (noliquid etc)
            for (let i = 0; i < mapData.layers.length; i++) {
                const layerData = mapData.layers[i];
                if (!layerData?.composition) continue;
                const layerType = i as LayerType;
                if (isBaseLayer(layerType) || !isCompositionLayer(layerType)) continue;

                this.addTileLayerForComposition(mapId, layerData.composition, {
                    id: `${LayerType[layerType]}-${mapId}`, visible: false, zIndex: 1, opacity: 0.85,
                    residentLodLevel: 4, cdnMissing: layerData.cdnMissing,
                });
            }

            this.loadDataLayers(mapId, mapData, thisGeneration);

            this.controlPanel.setCurrentVersion(mapData.version);
            this.controlPanel.setCurrentMap(mapId);
            this.controlPanel.updateLayers();

            if (mapData.isFallback && mapData.fallbackVersion) {
                this.controlPanel.showVersionFallbackWarning(mapData.requestedVersion, mapData.fallbackVersion);
            } else {
                this.controlPanel.hideVersionFallbackWarning();
            }

            this.scheduleRender();
            console.log(`Map ${mapId} loaded successfully`);
        } catch (error) {
            if (thisGeneration === this.mapLoadGeneration) {
                console.error(`Failed to load map ${mapId}:`, error);
                // TODO: Show error in UI (error skip if canceled from later load)
            }
        }
    }

    private async loadDataLayers(mapId: number, mapData: LoadedMapData, generation: number): Promise<void> {
        try {
            const impassEntry = mapData.layers[LayerType.Impass];
            const areaIdEntry = mapData.layers[LayerType.AreaId];

            const [impassData, areaIdData] = await Promise.all([
                impassEntry ? this.mapDataManager.loadBlob<ImpassDataDto>(impassEntry.hash) : null,
                areaIdEntry ? this.mapDataManager.loadBlob<AreaIdDataDto>(areaIdEntry.hash) : null,
            ]);

            if (generation !== this.mapLoadGeneration) return;

            if (impassData) {
                const impassLayer = new ImpassLayer({ id: `impass-${mapId}`, visible: true, zIndex: 10, opacity: 0.8 });
                impassLayer.setData(impassData);
                this.layerManager.addLayer(impassLayer);
            }

            if (areaIdData) {
                const highlightLayer = new AreaHighlightLayer(`area-highlight-${mapId}`);
                highlightLayer.setData(areaIdData);
                this.areaHighlightLayer = highlightLayer;
                this.layerManager.addLayer(highlightLayer);
                this.controlPanel.setZones(areaIdData);
            }

            this.controlPanel.updateLayers();
            this.scheduleRender();
        } catch (error) {
            console.warn(`Failed to load data layers for map ${mapId}:`, error);
        }
    }

    private addTileLayerForComposition(
        mapId: number,
        composition: MinimapComposition,
        options: Partial<Omit<TileLayerOptions, 'composition' | 'tileStreamer'>> = {}
    ): TileLayer {
        const layer = new TileLayerImpl({
            composition,
            tileStreamer: this.tileStreamer,
            id: options.id ?? `layer-${mapId}-${Date.now()}`,
            ...options,
        });

        this.layerManager.addLayer(layer);
        this.scheduleRender();
        return layer;
    }

    public setActiveBaseLayer(layerType: LayerType): void {
        if (!isBaseLayer(layerType)) return;

        for (const baseType of [LayerType.Minimap, LayerType.MapTexture]) {
            const layer = this.layerManager.getLayer(`base-${LayerType[baseType]}`);
            if (layer) {
                layer.visible = baseType === layerType;
            }
        }

        this.controlPanel.updateLayers();
        this.scheduleRender();
    }

    public getAvailableBaseLayers(): LayerType[] {
        if (!this.currentMapData) return [];
        return [LayerType.Minimap, LayerType.MapTexture]
            .filter(lt => this.currentMapData!.layers[lt]?.composition !== null && this.currentMapData!.layers[lt]?.composition !== undefined);
    }

    private handleMapChange(mapId: number): void {
        console.log(`Map changed to: ${mapId}`);
        this.currentMapId = mapId;
        this.flashOverlay.clear();

        // Load with the resolved version (not 'latest') so fallback detection works properly.
        // requestedVersion is preserved for URL display.
        this.loadMap(mapId, this.currentVersion, true).then(() => {
            this.updateURL();
        });
        this.scheduleRender();
    }

    private handleVersionChange(version: BuildVersion | 'latest'): void {
        const versionStr = version === 'latest' ? 'latest' : version.toString();
        console.log(`Version changed to: ${versionStr}`);
        this.currentVersion = version;
        this.requestedVersion = version;

        // Reload with new version, don't reposition, show flash for changed tiles
        this.loadMap(this.currentMapId, version, false, true).then(() => {
            this.updateURL();
        });
        this.scheduleRender();
    }

    private handleZoneHover(areaId: number | null): void {
        if (!this.areaHighlightLayer) return;

        if (areaId !== null) {
            this.areaHighlightLayer.highlightArea(areaId);
        } else {
            this.areaHighlightLayer.clearHighlight();
        }
        this.scheduleRender();
    }

    private handleZoneClick(areaId: number): void {
        if (!this.areaHighlightLayer) return;

        const raw = this.areaHighlightLayer.getBoundsForArea(areaId);
        if (!raw) return;

        const bounds = {
            ...raw,
            width: raw.maxX - raw.minX,
            height: raw.maxY - raw.minY,
            centerX: (raw.minX + raw.maxX) / 2,
            centerY: (raw.minY + raw.maxY) / 2,
        };
        this.cameraController.fitToBounds(bounds, 10, 1.0);

        this.areaHighlightLayer.highlightArea(areaId);
        this.scheduleRender();

        // Brief flash then clear
        setTimeout(() => {
            this.areaHighlightLayer?.clearHighlight();
            this.scheduleRender();
        }, 1500);
    }

    private updateURL(): void {
        const camera = this.cameraController.getPos();
        const wowCoords = CoordinateTranslator.internalToWow(camera.centerX, camera.centerY);

        let accuracy = 0;
        if (camera.zoom <= 0.2) {
            accuracy = 3;
        } else if (camera.zoom <= 1.0) {
            accuracy = 2;
        } else if (camera.zoom <= 1.5) {
            accuracy = 1;
        }

        const xStr = wowCoords.x.toFixed(accuracy);
        const yStr = wowCoords.y.toFixed(accuracy);
        let zoomStr = camera.zoom.toFixed(4);
        zoomStr = zoomStr.replace(/\.?0+$/, '');

        // Use requestedVersion for URL (human-readable format or 'latest')
        const versionStr = this.requestedVersion === 'latest' ? 'latest' : this.requestedVersion.toString();
        const newPath = `/map/${this.currentMapId}/${versionStr}/${xStr}/${yStr}/${zoomStr}`;
        const url = new URL(window.location.href);
        url.pathname = newPath;
        url.search = '';
        window.history.replaceState({}, '', url.toString());
    }

    private resizeCanvas(): void {
        const displayWidth = this.canvas.clientWidth;
        const displayHeight = this.canvas.clientHeight;
        if (this.canvas.width !== displayWidth || this.canvas.height !== displayHeight) {
            this.canvas.width = displayWidth;
            this.canvas.height = displayHeight;

            this.gl.viewport(0, 0, this.canvas.width, this.canvas.height);
            this.doRender(); // immediate full render to avoid black frame flash
        }
    }

    private startRenderLoop(): void {
        const render = () => {
            if (this.needsRender) {
                this.doRender();
            }
            this.animationId = requestAnimationFrame(render);
        };
        render();
    }

    private scheduleRender(): void {
        this.needsRender = true;
    }

    private doRender(): void {
        const currentTime = performance.now();
        const deltaTime = Math.min(currentTime - this.lastFrameTime, 50);
        this.lastFrameTime = currentTime;
        const camera = this.cameraController.getPos();

        const canvasSize = {
            width: this.canvas.width,
            height: this.canvas.height,
        };
        const renderContext: RenderContext = {
            camera,
            canvasSize,
            deltaTime,
            lodBias: this.renderer.lodBias, // todo: move, user config?
        };

        this.renderQueue.clear();
        const visibleLayers = this.layerManager.getVisibleLayers();
        for (const layer of visibleLayers) {
            layer.queueRenderCommands(this.renderQueue, renderContext);
        }

        this.renderer.renderQueue(camera, this.renderQueue);

        // render flash overlay
        const flashActive = this.flashOverlay.update(deltaTime);
        if (this.flashOverlay.hasActiveFlashes()) {
            const flashes = this.flashOverlay.getActiveFlashes();
            this.renderer.renderFlashOverlay(flashes, camera, currentTime / 1000.0);
        }

        // debug frame ticker
        if (this.debugPanel?.showFrameTicker) {
            this.renderer.renderFrameTicker();
        }

        // re-render until flash done
        this.needsRender = flashActive;
    }

    dispose(): void {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
        }
        this.cameraController.detachCanvas();
        this.controlPanel.dispose();
        this.debugPanel?.dispose();
        this.mapDataManager.clearCache();
    }
}
