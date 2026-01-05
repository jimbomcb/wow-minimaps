import { TileStreamer } from './tile-streamer.js';
import { CameraController } from './camera-controller.js';

export interface DebugPanelOptions {
    container: HTMLElement;
    enabled: boolean;
}

// Dev only debug panel, just misc internal state stuff, nothing interesting for explorers
export class DebugPanel {
    private container: HTMLElement;
    private panelElement: HTMLElement | null = null;
    private enabled: boolean;
    private visible: boolean = true;
    private updateInterval: number | null = null;
    private readonly UPDATE_INTERVAL_MS = 250;

    private tileStreamer: TileStreamer | null = null;
    private cameraController: CameraController | null = null;

    private tileStreamerContent: HTMLElement | null = null;
    private cameraContent: HTMLElement | null = null;

    constructor(options: DebugPanelOptions) {
        this.container = options.container;
        this.enabled = options.enabled;

        if (this.enabled) {
            this.createPanel();
            this.startUpdateLoop();
        }
    }

    private createPanel(): void {
        this.panelElement = document.createElement('div');
        this.panelElement.className = 'debug-panel';
        this.panelElement.innerHTML = `
            <div class="debug-panel-header">
                <span>Debug</span>
                <button class="debug-panel-toggle">−</button>
            </div>
            <div class="debug-panel-content"></div>
        `;

        const toggleBtn = this.panelElement.querySelector('.debug-panel-toggle') as HTMLButtonElement;
        toggleBtn.addEventListener('click', () => {
            this.visible = !this.visible;
            const content = this.panelElement?.querySelector('.debug-panel-content') as HTMLElement;
            if (content) content.style.display = this.visible ? 'block' : 'none';
            toggleBtn.textContent = this.visible ? '−' : '+';
        });
        this.container.appendChild(this.panelElement);
    }

    setTileStreamer(streamer: TileStreamer): void {
        this.tileStreamer = streamer;
        const details = document.createElement('details');
        details.innerHTML = `<summary>Tile Streamer</summary><div class="debug-section-content"></div>`;
        this.tileStreamerContent = details.querySelector('.debug-section-content');
        this.panelElement?.querySelector('.debug-panel-content')?.appendChild(details);
    }

    setCameraController(controller: CameraController): void {
        this.cameraController = controller;
        const details = document.createElement('details');
        details.innerHTML = `<summary>Camera</summary><div class="debug-section-content"></div>`;
        this.cameraContent = details.querySelector('.debug-section-content');
        this.panelElement?.querySelector('.debug-panel-content')?.appendChild(details);
    }

    private startUpdateLoop(): void {
        this.updateInterval = window.setInterval(() => this.update(), this.UPDATE_INTERVAL_MS);
    }

    private update(): void {
        if (!this.enabled || !this.visible) return;

        if (this.tileStreamer && this.tileStreamerContent) {
            const stats = this.tileStreamer.getStats();
            this.tileStreamerContent.innerHTML = `
                <div class="debug-row"><span class="debug-label">Cached:</span><span class="debug-value">${stats.cachedTextures}</span></div>
                <div class="debug-row"><span class="debug-label">Loading:</span><span class="debug-value">${stats.currentLoads}</span></div>
                <div class="debug-row"><span class="debug-label">Queued:</span><span class="debug-value">${stats.pendingQueue}</span></div>
                <div class="debug-row"><span class="debug-label">Resident:</span><span class="debug-value">${stats.residentTiles}</span></div>
                <div class="debug-row"><span class="debug-label">GPU Memory:</span><span class="debug-value">${stats.gpuMemoryMB} / ${stats.gpuMemoryBudgetMB} MB (${stats.gpuMemoryUsagePercent}%)</span></div>
            `;
        }

        if (this.cameraController && this.cameraContent) {
            const pos = this.cameraController.getPos();
            const lod = Math.min(6, Math.max(0, Math.floor(Math.log2(pos.zoom))));
            this.cameraContent.innerHTML = `
                <div class="debug-row"><span class="debug-label">Center X:</span><span class="debug-value">${pos.centerX.toFixed(2)}</span></div>
                <div class="debug-row"><span class="debug-label">Center Y:</span><span class="debug-value">${pos.centerY.toFixed(2)}</span></div>
                <div class="debug-row"><span class="debug-label">Zoom:</span><span class="debug-value">${pos.zoom.toFixed(4)}</span></div>
                <div class="debug-row"><span class="debug-label">Optimal LOD:</span><span class="debug-value">${lod}</span></div>
            `;
        }
    }

    setEnabled(enabled: boolean): void {
        this.enabled = enabled;
        if (this.panelElement) {
            this.panelElement.style.display = enabled ? 'flex' : 'none';
        }
        if (enabled && !this.panelElement) {
            this.createPanel();
            this.startUpdateLoop();
        }
    }

    isEnabled(): boolean {
        return this.enabled;
    }

    dispose(): void {
        if (this.updateInterval !== null) {
            clearInterval(this.updateInterval);
            this.updateInterval = null;
        }
        this.panelElement?.remove();
        this.panelElement = null;
    }
}
