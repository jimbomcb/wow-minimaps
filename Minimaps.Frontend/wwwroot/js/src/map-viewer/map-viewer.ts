import { MapViewerOptions } from './types.js';
import { Renderer } from './renderer.js';
import { BuildVersion } from './build-version.js';
import { CameraController } from './camera-controller.js';
import { LayerManager } from './layer-manager.js';
import { TileStreamer, TileRequest } from './tile-streamer.js';
import { TileLayer } from './layers/tile-layer.js';
import { isTileLayer } from './layers/layers.js';

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
        const newUrl = `/map/${pathParts[1]}/latest${window.location.search}${window.location.hash}`;
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
    
    const versionString = typeof version === 'string' ? version : version.encodedValueString;
    const mapViewerInstance = new MapViewer({
        container: canvas,
        mapId: mapId,
        version: versionString
    });

    return () => {
        mapViewerInstance.dispose();
    };
}

interface TileLayerOptions {
    id?: string;
    visible?: boolean;
    opacity?: number;
    zIndex?: number;
    lodLevel?: number;
    //parentLayer?: TileLayer | undefined;
}

export class MapViewer {
    private canvas: HTMLCanvasElement;
    private renderer: Renderer;
    private cameraController: CameraController;
    private animationId?: number;
    private gl: WebGL2RenderingContext;
    private lastTileRequest = 0;
    private needsRender = true;
    private layerManager: LayerManager;
    private tileStreamer: TileStreamer;
    private pendingRequests: TileRequest[] = []; // Store the last frame requests

    private footerOverlay: HTMLElement | null = null;
    private lastDebugUpdate = 0;
    private readonly DEBUG_UPDATE_THROTTLE = 250; // ms
    private currentLoadedTiles: any[] = [];

    constructor(options: MapViewerOptions) {
        this.canvas = options.container;
        this.gl = this.canvas.getContext('webgl2')!;
        if (!this.gl) throw new Error('WebGL2 not supported');

        this.renderer = new Renderer(this.canvas);
        this.resizeCanvas();

        this.layerManager = new LayerManager();
        this.tileStreamer = new TileStreamer(this.gl);
        this.tileStreamer.setTextureLoadedCallback(() => {
            // new texture becoming available in the streamer rerenders 
            this.updateCurrentLoadedTiles(); // todo: clean up reconciling the desired tile list with what's available
            this.scheduleRender();
        });

        this.cameraController = new CameraController({
            centerX: 32,
            centerY: 32,
            zoom: 1
        });
        this.cameraController.attachCanvas(this.canvas, () => this.resizeCanvas());
        this.cameraController.onCameraMoved((_) => {
            this.updateDebugOverlay();
            this.scheduleRender();
            this.requestVisibleTiles();
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
            zIndex: 0
        });
    }

    public addTileLayer(mapId: number, version: string, options: Partial<TileLayerOptions> = {}): TileLayer {
        const layer = new TileLayer({
            id: options.id || `layer-${mapId}-${version}-${Date.now()}`,
            mapId,
            version,
            visible: options.visible ?? true,
            opacity: options.opacity ?? 1.0,
            zIndex: options.zIndex ?? 0,
            lodLevel: options.lodLevel ?? 0,
            //parentLayer: options.parentLayer ?? undefined
        });

        this.layerManager.addLayer(layer);

        // keep a baseline LOD level in memory for LOD fallback
        const loadingPromise = layer.getLoadingPromise();
        if (loadingPromise) {
            loadingPromise.then(() => {
                this.initializeBaseTiles(layer);
            }).catch(error => {
                console.error(`Failed to load fallback tiles for layer ${layer.id}:`, error);
            });
        }

        this.requestVisibleTiles();
        return layer;
    }

    public removeTileLayer(layerId: string): void {
        this.layerManager.removeLayer(layerId);
        this.requestVisibleTiles();
    }

    private initializeBaseTiles(layer: TileLayer): void {
        // todo: configuring, LOD5?
        const composition = layer.getComposition();
        if (composition) {
            const lod5Data = composition.getLODData(5);
            if (lod5Data) {
                for (const [hash] of lod5Data) {
                    this.tileStreamer.markResident(hash);
                }
            }
        }
    }

    private requestVisibleTiles(): void {
        const now = Date.now();
        if (now - this.lastTileRequest < 100) {
            return;
        }

        this.lastTileRequest = now;

        const camera = this.cameraController.getPos();
        const canvasSize = {
            width: this.canvas.width,
            height: this.canvas.height
        };

        const allRequests = [];
        const tileLayers = this.layerManager.getLayersOfType(isTileLayer);
        for (const layer of tileLayers) {
            if (layer.isLoaded()) {
                const layerRequests = layer.calculateVisibleTiles(camera, canvasSize);
                allRequests.push(...layerRequests);
            }
        }

        // pass latest request data to the texture streamer
        this.pendingRequests = allRequests;
        this.currentLoadedTiles = this.tileStreamer.processFrameRequirements(allRequests);
    }

    private updateDebugOverlay(): void {
        if (!this.footerOverlay) return;

        const now = Date.now();
        if (now - this.lastDebugUpdate < this.DEBUG_UPDATE_THROTTLE) {
            return;
        }
        this.lastDebugUpdate = now;

        let debugText = ``;
        const stats = this.tileStreamer.getStats();
        debugText += `Textures: ${stats.cachedTextures} cached, ${stats.currentLoads} loading`;
        if (stats.residentTiles > 0) {
            debugText += ` (${stats.residentTiles} resident)`;
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
        const camera = this.cameraController.getPos();

        // todo: think about layer handling, keying
        const tilesToRender = this.currentLoadedTiles.map(tile => ({
            tileKey: `${tile.worldX}-${tile.worldY}-${tile.lodLevel}`,
            texture: tile.texture,
            x: tile.worldX,
            y: tile.worldY,
            zoom: tile.lodLevel
        }));
        this.renderer.render(camera, tilesToRender);
        this.needsRender = false;
    }

    private updateCurrentLoadedTiles(): void {
        // todo: streamer cleanup
        if (this.pendingRequests.length > 0) {
            const loadedTiles = this.tileStreamer.processFrameRequirements(this.pendingRequests);
            this.currentLoadedTiles = loadedTiles;
        }
    }

    dispose(): void {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
        }
        this.cameraController.detachCanvas();
    }
}