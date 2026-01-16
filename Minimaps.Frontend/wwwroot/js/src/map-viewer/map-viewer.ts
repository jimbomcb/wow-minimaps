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
import { MapDataManager } from './map-data-manager.js';
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
    private mapLoadGeneration: number = 0;

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

            this.currentVersion = mapData.version;

            const tileLayers = this.layerManager.getLayersOfType(isTileLayer);
            for (const layer of tileLayers) {
                layer.dispose();
                this.layerManager.removeLayer(layer.id);
            }

            // Add a monochrome parent layer under the actual map data
            if (mapData.parentComposition && mapData.parentMapId !== null) {
                this.addTileLayerForComposition(mapData.parentMapId, mapData.parentComposition, {
                    id: `parent-${mapData.parentMapId}`,
                    zIndex: -1,
                    residentLodLevel: 4,
                    monochrome: true,
                    debugSkipLODs: [],
                });
            }

            // Main map layer
            this.addTileLayerForComposition(mapId, mapData.composition, {
                id: 'main',
                zIndex: 0,
                residentLodLevel: 4,
                debugSkipLODs: [],
            });

            // Composition diff flash
            this.currentComposition = mapData.composition;
            if (oldComposition) {
                const changes = MinimapComposition.diff(oldComposition, mapData.composition);
                const totalChanges = changes.added.size + changes.modified.size + changes.removed.size;
                if (totalChanges > 0) {
                    //console.log(`Flash: ${changes.added.size} added, ${changes.modified.size} modified, ${changes.removed.size} removed`);
                    this.flashOverlay.clear();
                    this.flashOverlay.triggerFlash(changes);
                }
            }

            this.controlPanel.setCurrentMap(mapId);
            this.controlPanel.updateLayers();

            if (autoZoom && mapData.composition.bounds) {
                this.cameraController.fitToBounds(mapData.composition.bounds, 10);
            }

            // fallback warning if we're snapping to a different version
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

    private addTileLayerForComposition(
        mapId: number,
        composition: MinimapComposition,
        options: Omit<Partial<TileLayerOptions>, 'composition' | 'tileStreamer'> = {}
    ): TileLayer {
        const layer = new TileLayerImpl({
            id: options.id || `layer-${mapId}-${Date.now()}`,
            composition,
            tileStreamer: this.tileStreamer,
            visible: options.visible ?? true,
            opacity: options.opacity ?? 1.0,
            zIndex: options.zIndex ?? 0,
            lodLevel: options.lodLevel ?? 0,
            residentLodLevel: options.residentLodLevel ?? 5,
            monochrome: options.monochrome ?? false,
            ...(options.debugSkipLODs && { debugSkipLODs: options.debugSkipLODs }),
        });

        this.layerManager.addLayer(layer);
        this.scheduleRender();
        return layer;
    }

    private handleMapChange(mapId: number): void {
        console.log(`Map changed to: ${mapId}`);
        this.currentMapId = mapId;
        this.requestedVersion = this.currentVersion;
        this.flashOverlay.clear();

        // Reload the map with the new map, auto-zoom to fit
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
            this.renderer.renderFlashOverlay(flashes, camera);
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
