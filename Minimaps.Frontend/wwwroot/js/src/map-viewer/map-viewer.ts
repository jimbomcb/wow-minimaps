import { MapViewerOptions } from './types.js';
import { Renderer } from './renderer.js';
import { BuildVersion } from './build-version.js';
import { CameraController } from './camera-controller.js';
import { LayerManager } from './layer-manager.js';
import { TileStreamer, TileRequest } from './tile-streamer.js';
import { TileLayerImpl, TileLayerOptions } from './layers/tile-layer.js';
import { isTileLayer, TileLayer } from './layers/layers.js';
import { RenderContext } from "./layers/layers.js";
import { RenderQueue } from "./render-queue.js";
import { CoordinateTranslator } from './coordinate-translator.js';

export async function MapViewerInit() {
    const canvas = document.getElementById('map-canvas') as HTMLCanvasElement;
    if (!canvas) {
        console.error("missing #map-canvas");
        return;
    }

    const pathParts = window.location.pathname.split('/').filter(part => part.length > 0);
    if (pathParts.length < 2 || pathParts[0] !== 'map') {
        console.error("invalid URL format, expected /map/{mapId}/{version}");
        return;
    }

    if (pathParts.length === 2) {
        const newUrl = `/map/${pathParts[1]}/latest${window.location.hash}`;
        window.history.replaceState({}, '', newUrl);
    }
        
    const mapIdStr = pathParts[1];
    if (!mapIdStr) {
        console.error("missing mapId in URL");
        return;
    }
    const mapId = parseInt(mapIdStr, 10);
    
    var version : string | BuildVersion = "latest";
    if (pathParts.length > 2) {
        var versionStr = pathParts[2];
        if (!versionStr) {
            console.error("invalid version in URL");
            return;
        }
        const buildVer = BuildVersion.tryParse(versionStr);
        if (buildVer !== null) {
            console.log("Querying exact version ", buildVer);
            version = buildVer;
        }
        else {
            console.log("Querying tagged version ", versionStr);
            version = versionStr; // fall back to tag lookup
        }
    }
    
    // Parse from URL: /map/{mapId}/{version}/{x}/{y}/{zoom}
    let initialCamera = {
        centerX: 32,
        centerY: 32,
        zoom: 30
    };

    if (pathParts[3] !== undefined && pathParts[4] !== undefined) {
        const wowX = parseFloat(pathParts[3]);
        const wowY = parseFloat(pathParts[4]);
        const internal = CoordinateTranslator.wowToInternal(wowX, wowY);
        initialCamera.centerX = internal.x;
        initialCamera.centerY = internal.y;
    }
    
    if (pathParts[5] !== undefined) {
        initialCamera.zoom = parseFloat(pathParts[5]);
    }

    const versionString = typeof version === 'string' ? version : version.encodedValueString;
    const mapViewerInstance = new MapViewer({
        container: canvas,
        mapId: mapId,
        version: versionString,
        initPosition: initialCamera
    });

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

    private footerOverlay: HTMLElement | null = null;
    private lastDebugUpdate = 0;
    private readonly DEBUG_UPDATE_THROTTLE = 250; // ms

    private renderQueue: RenderQueue;
    private lastFrameTime: number = 0;

    constructor(options: MapViewerOptions) {
        this.canvas = options.container;
        this.gl = this.canvas.getContext('webgl2')!;
        if (!this.gl) throw new Error('WebGL2 not supported');

        this.renderQueue = new RenderQueue();
        this.renderer = new Renderer(this.canvas);
        this.resizeCanvas();

        this.layerManager = new LayerManager();
        this.tileStreamer = new TileStreamer(this.gl);
        this.tileStreamer.setTextureLoadedCallback(() => {
            // new texture becoming available in the streamer rerenders 
            this.scheduleRender();
        });

        this.cameraController = new CameraController({
            centerX: options.initPosition?.centerX ?? 32,
            centerY: options.initPosition?.centerY ?? 32,
            zoom: options.initPosition?.zoom ?? 30
        });
        this.cameraController.attachCanvas(this.canvas, () => this.resizeCanvas());
        this.cameraController.onCameraMoved((_) => {
            this.updateDebugOverlay();
            this.scheduleRender();
        });
        this.cameraController.onCameraReleased((camera) => {
            this.updateURLWithCamera(camera);
        });

        this.footerOverlay = document.getElementById('map-footer-overlay');
        if (this.footerOverlay) {
            this.updateDebugOverlay();
        }

        this.startRenderLoop();

        // temp for now initial single layer based on url path
        // todo: parsing backend response for current map's parent, add as layer behind w fade
        const versionString = typeof options.version === 'string' ? options.version : options.version.encodedValueString;
        this.addTileLayer(options.mapId, versionString, {
            id: 'main',
            zIndex: 0,
            residentLodLevel: 4,
            debugSkipLODs: []
        });
        //this.addTileLayer(2962, versionString, {
        //    id: 'test',
        //    zIndex: 2,
        //    residentLodLevel: 4,
        //    debugSkipLODs: []
        //});
    }

    public addTileLayer(mapId: number, version: string, options: Omit<Partial<TileLayerOptions>, 'tileStreamer'> = {}): TileLayer {
        const layer = new TileLayerImpl({
            id: options.id || `layer-${mapId}-${version}-${Date.now()}`,
            mapId,
            version,
            tileStreamer: this.tileStreamer,
            visible: options.visible ?? true,
            opacity: options.opacity ?? 1.0,
            zIndex: options.zIndex ?? 0,
            lodLevel: options.lodLevel ?? 0,
            residentLodLevel: options.residentLodLevel ?? 5, // Default LOD5 as resident
            ...(options.debugSkipLODs && { debugSkipLODs: options.debugSkipLODs }),
        });

        this.layerManager.addLayer(layer);

        this.scheduleRender();
        return layer;
    }

    public removeTileLayer(layerId: string): void {
        this.layerManager.removeLayer(layerId);
        this.scheduleRender();
    }

    private updateDebugOverlay(): void {
        if (!this.footerOverlay) return;

        const now = Date.now();
        if (now - this.lastDebugUpdate < this.DEBUG_UPDATE_THROTTLE) {
            return;
        }
        this.lastDebugUpdate = now;

        const camera = this.cameraController.getPos();
        const canvasSize = {
            width: this.canvas.width,
            height: this.canvas.height
        };

        let debugText = ``;
        const stats = this.tileStreamer.getStats();
        debugText += `Textures: ${stats.cachedTextures} cached, ${stats.currentLoads} loading`;
        if (stats.residentTiles > 0) {
            debugText += ` (${stats.residentTiles} resident)`;
        }
        if (stats.pendingQueue > 0) {
            debugText += ` [${stats.pendingQueue} queued]`;
        }
        
        const tileLayers = this.layerManager.getLayersOfType(isTileLayer);
        const loadedLayers = tileLayers.filter(layer => layer.isLoaded());
        const errorLayers = tileLayers.filter(layer => layer.hasError());
        
        if (tileLayers.length > 1) {
            debugText += ` | Layers: ${loadedLayers.length}/${tileLayers.length}`;
        }
        if (errorLayers.length > 0) {
            debugText += ` (${errorLayers.length} errors)`;
        }

        const pixelsPerTile = 512 / camera.zoom;
        const biasedZoom = camera.zoom / this.renderer.lodBias;
        const optimalLOD = Math.max(0, Math.floor(Math.log2(biasedZoom)));
        debugText += ` | Zoom: ${camera.zoom.toFixed(2)}x`;
        if (this.renderer.lodBias !== 1.0) {
            debugText += ` (bias: ${this.renderer.lodBias}x, actual: ${biasedZoom.toFixed(2)}x)`;
        }
        debugText += ` (${pixelsPerTile.toFixed(0)}px/tile, LOD${Math.min(6, optimalLOD)})`;

        const debugContent = this.footerOverlay.querySelector('.debug-tilemap');
        if (debugContent) {
            debugContent.textContent = debugText;
        } else {
            this.footerOverlay.textContent = debugText;
        }
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
            this.updateDebugOverlay();
            this.animationId = requestAnimationFrame(render);
        };
        render();
    }

    private scheduleRender(): void {
        this.needsRender = true;
    }

    private doRender(): void {
        const currentTime = performance.now();
        const deltaTime = currentTime - this.lastFrameTime;
        this.lastFrameTime = currentTime;
        const camera = this.cameraController.getPos();

        const canvasSize = {
            width: this.canvas.width,
            height: this.canvas.height
        };
        const renderContext: RenderContext = {
            camera,
            canvasSize,
            deltaTime,
            lodBias: this.renderer.lodBias // todo: move, user config?
        };

        this.renderQueue.clear();
        const visibleLayers = this.layerManager.getVisibleLayers();
        for (const layer of visibleLayers) {
            layer.queueRenderCommands(this.renderQueue, renderContext);
        }

        this.renderer.renderQueue(camera, this.renderQueue);
        this.needsRender = false;
    }

    private updateURLWithCamera(camera: { centerX: number, centerY: number, zoom: number }): void {
        const pathParts = window.location.pathname.split('/').filter(part => part.length > 0);
        if (pathParts.length < 3) return; // Need at least /map/{mapId}/{version}
        
        const wowCoords = CoordinateTranslator.internalToWow(camera.centerX, camera.centerY);

        // use a higher accuracy when we're zoomed beyond 1:1 pixel to base LOD0 texel
        const accuracy = camera.zoom < 1.0 ? 2 : 0;
        let xStr = wowCoords.x.toFixed(accuracy);
        let yStr = wowCoords.y.toFixed(accuracy);
        let zoomStr = camera.zoom.toFixed(4);
        
        const newPath = `/map/${pathParts[1]}/${pathParts[2]}/${xStr}/${yStr}/${zoomStr}`;
        const url = new URL(window.location.href);
        url.pathname = newPath;
        url.search = '';
        window.history.replaceState({}, '', url.toString());
    }

    dispose(): void {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
        }
        this.cameraController.detachCanvas();
    }
}