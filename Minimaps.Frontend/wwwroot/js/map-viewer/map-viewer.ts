import { MapViewerOptions, MapViewport } from './types.js';
import { TileLoader } from './tile-loader.js';
import { Renderer } from './renderer.js';
import { TileManager } from './tile-manager.js';
import { CoordinateTranslator } from './coordinate-translator.js';
import { BuildVersion } from './build-version.js';

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

    const mapId = parseInt(pathParts[1], 10);
    var version : string | BuildVersion = "latest";
    if (pathParts.length > 2) {
        var versionStr = pathParts[2];
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
    
    const urlParams = new URLSearchParams(window.location.search);
    const initialViewport = CoordinateTranslator.parseViewportFromUrl(urlParams);
    
    const mapViewerInstance = new MapViewer({
        container: canvas,
        mapId: mapId,
        version: version,
        initialViewport: initialViewport
    });

    mapViewerInstance.setViewportChangedCallback(function (viewport) {
        // todo
    });

    return () => {
        mapViewerInstance.dispose();
    };
}

export class MapViewer {
    private loaded: boolean;
    private canvas: HTMLCanvasElement;
    private renderer: Renderer;
    private tileLoader: TileLoader;
    private tileManager: TileManager;
    private viewport: MapViewport;
    private animationId?: number;
    private loaderPromise: Promise<TileLoader>;
    private gl: WebGL2RenderingContext | null = null;
    private lastTileRequest = 0;
    private needsRender = true;

    private lastUrlUpdate = 0;
    private readonly URL_UPDATE_THROTTLE = 50;
    private onViewportChanged?: (viewport: MapViewport) => void;

    private footerOverlay: HTMLElement | null = null;
    private lastDebugUpdate = 0;
    private readonly DEBUG_UPDATE_THROTTLE = 250;

    constructor(options: MapViewerOptions) {
        this.canvas = options.container;
        this.resizeCanvas();
        this.gl = this.canvas.getContext('webgl2');
        this.renderer = new Renderer(this.canvas);
        
        const versionString = typeof options.version === 'string' 
            ? options.version 
            : options.version.encodedValueString;
        this.loaderPromise = TileLoader.forVersion(options.mapId, versionString);

        this.footerOverlay = document.getElementById('map-footer-overlay');
        if (this.footerOverlay) {
            this.updateDebugOverlay();
        }

        this.setupEventHandlers();
        this.startRenderLoop();

        this.viewport = this.constrainViewport(options.initialViewport || {
            centerX: 32,
            centerY: 32,
            altitude: 1.0
        });

        if (options.initialViewport) {
            this.updateUrlWithViewport(true);
        }

        this.loaded = false;
        this.loaderPromise.then(loader => {
            this.tileLoader = loader;
            this.tileManager = new TileManager(loader, this.renderer);
            this.tileManager.setRenderCallback(() => this.scheduleRender());
            this.requestVisibleTiles();
            this.loaded = true;
        }).catch(error => {
            console.error("Failed to initialize TileLoader:", error);
        });
    }

    private updateDebugOverlay(): void {
        if (!this.footerOverlay || !this.loaded) return;

        const now = Date.now();
        if (now - this.lastDebugUpdate < this.DEBUG_UPDATE_THROTTLE) {
            return;
        }
        this.lastDebugUpdate = now;

        let debugText = ``;
        if (this.loaded && this.tileManager) {
            const stats = this.tileManager.getStats();
            debugText += `Tiles: ${stats.loadedTiles}/${stats.totalTiles}`;
            if (stats.loadingTiles > 0) {
                debugText += ` (${stats.loadingTiles} loading)`;
            }
            if (stats.queuedTiles > 0) {
                debugText += ` (${stats.queuedTiles} queued)`;
            }
            if (stats.failedTiles > 0) {
                debugText += ` (${stats.failedTiles} failed)`;
            }
        }

        const debugContent = this.footerOverlay.querySelector('.debug-tilemap');
        if (debugContent) {
            debugContent.textContent = debugText;
        } else {
            this.footerOverlay.textContent = debugText;
        }
    }

    private updateUrlWithViewport(forceUpdate = false): void {
        const now = Date.now();

        // Excessive state pushing results in chrome ignoring them due to their abuse to hang,
        // so we're going to limit it to once every 50ms dragging, and once on release
        if (!forceUpdate && now - this.lastUrlUpdate < this.URL_UPDATE_THROTTLE) {
            return; 
        }

        this.lastUrlUpdate = now;
        
        const url = new URL(window.location.href);
        const urlParams = CoordinateTranslator.viewportToUrlParams(this.viewport);
        
        url.search = urlParams;
        window.history.replaceState({}, '', url.toString());
    }

    private resizeCanvas(): void {
        const displayWidth = this.canvas.clientWidth;
        const displayHeight = this.canvas.clientHeight;
        if (this.canvas.width !== displayWidth || this.canvas.height !== displayHeight) {
            this.canvas.width = displayWidth;
            this.canvas.height = displayHeight;

            if (this.loaded) {
                this.gl?.viewport(0, 0, this.canvas.width, this.canvas.height);
                this.doRender(); // immediate full render to avoid black frame flash
            } else {
                this.scheduleRender();
            }
        }
    }

    private requestVisibleTiles(): void {
        if (!this.tileLoader || !this.tileManager) return;

        const now = Date.now();
        if (now - this.lastTileRequest < 100) {
            return;
        }
        this.lastTileRequest = now;

        const canvasSize = {
            width: this.canvas.width,
            height: this.canvas.height
        };

        const visibleTiles = this.tileLoader.getVisibleTilesWithPriority(this.viewport, canvasSize);
        
        for (const tile of visibleTiles) {
            this.tileManager.requestTile(tile.hash, tile.x, tile.y, tile.zoom, tile.priority);
        }
    }

    setViewport(viewport: MapViewport): void {
        this.viewport = { ...viewport };
        this.onViewportChanged?.(this.viewport);
        this.updateUrlWithViewport(true);
        this.updateDebugOverlay();
        this.scheduleRender();
        this.requestVisibleTiles();
    }

    private startRenderLoop(): void {
        const render = () => {
            if (this.loaded) {
                const isDirty = this.tileManager.isDirtyAndClear();
                if (this.needsRender || isDirty) {
                    this.doRender();
                }
            }
            this.updateDebugOverlay();
            this.animationId = requestAnimationFrame(render);
        };
        render();
    }

    // queue a draw on the next render loop
    private scheduleRender(): void {
        this.needsRender = true;
    }

    // immediate render to canvas
    private doRender(): void {
        console.assert(this.loaded, "rendering pre-load");
        const loadedTiles = this.tileManager.getLoadedTiles();
        this.renderer.render(this.viewport, loadedTiles);
        this.needsRender = false;
    }

    setViewportChangedCallback(callback: (viewport: MapViewport) => void): void {
        this.onViewportChanged = callback;
    }

    private getCanvasMousePosition(clientX: number, clientY: number): { x: number, y: number } {
        const rect = this.canvas.getBoundingClientRect();
        return {
            x: clientX - rect.left,
            y: clientY - rect.top
        };
    }

    private canvasPositionToWorldPosition(canvasX: number, canvasY: number): { x: number, y: number } {
        const canvasWidth = this.canvas.width;
        const canvasHeight = this.canvas.height;
        
        const offsetX = canvasX - canvasWidth / 2;
        const offsetY = canvasY - canvasHeight / 2;
        
        const unitsPerPixel = this.viewport.altitude / 512;
        const worldX = this.viewport.centerX + (offsetX * unitsPerPixel);
        const worldY = this.viewport.centerY + (offsetY * unitsPerPixel);
        
        return { x: worldX, y: worldY };
    }

    private setupEventHandlers(): void {
        let isDragging = false;
        let lastX = 0;
        let lastY = 0;

        const resizeObserver = new ResizeObserver(() => {
            this.resizeCanvas();
        });
        resizeObserver.observe(this.canvas);

        this.canvas.addEventListener('mousedown', (e) => {
            isDragging = true;
            lastX = e.clientX;
            lastY = e.clientY;
        });

        this.canvas.addEventListener('mousemove', (e) => {
            if (isDragging) {
                const deltaX = e.clientX - lastX;
                const deltaY = e.clientY - lastY;
                const unitsPerPixel = this.viewport.altitude / 512;
                
                const worldDeltaX = deltaX * unitsPerPixel;
                const worldDeltaY = deltaY * unitsPerPixel;
                
                const constrainedViewport = this.constrainViewport({
                    ...this.viewport,
                    centerX: this.viewport.centerX - worldDeltaX,
                    centerY: this.viewport.centerY - worldDeltaY
                });
                
                this.viewport = constrainedViewport;
                
                lastX = e.clientX;
                lastY = e.clientY;
                
                this.updateUrlWithViewport(false);
                this.onViewportChanged?.(this.viewport);
                this.scheduleRender();
                this.requestVisibleTiles();
            }
        });

        this.canvas.addEventListener('mouseup', () => {
            if (isDragging) {
                isDragging = false;
                this.updateUrlWithViewport(true);
            }
        });

        this.canvas.addEventListener('wheel', (e) => {
            e.preventDefault();

            // cursor pos based scrolling
            const mouseCanvas = this.getCanvasMousePosition(e.clientX, e.clientY);
            const worldMousePos = this.canvasPositionToWorldPosition(mouseCanvas.x, mouseCanvas.y);
            
            const zoomFactor = e.deltaY > 0 ? 1.1 : 0.9;
            const newAltitude = this.viewport.altitude * zoomFactor;
            const clampedAltitude = Math.max(0.1, Math.min(50, newAltitude));
            
            const altitudeRatio = clampedAltitude / this.viewport.altitude;
            const newCenterX = worldMousePos.x + (this.viewport.centerX - worldMousePos.x) * altitudeRatio;
            const newCenterY = worldMousePos.y + (this.viewport.centerY - worldMousePos.y) * altitudeRatio;
            
            const constrainedViewport = this.constrainViewport({
                centerX: newCenterX,
                centerY: newCenterY,
                altitude: clampedAltitude
            });
            
            this.viewport = constrainedViewport;
            
            this.updateUrlWithViewport(true);
            this.onViewportChanged?.(this.viewport);
            this.updateDebugOverlay();
            this.scheduleRender();
            this.requestVisibleTiles();
        });
    }

    private constrainViewport(viewport: MapViewport): MapViewport {
        return viewport;
    }

    dispose(): void {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
        }
    }
}