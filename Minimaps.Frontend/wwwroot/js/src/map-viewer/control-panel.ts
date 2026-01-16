import type { MapDataManager, MapInfo } from './map-data-manager.js';
import type { LayerManager } from './layer-manager.js';
import type { TileLayer } from './layers/layers.js';
import { isTileLayer } from './layers/layers.js';
import { BuildVersion } from './build-version.js';

export interface VersionInfo {
    version: BuildVersion;
    displayName: string;
    compositionHash: string;
    products: string[];
}

export interface LayerDisplayInfo {
    layerId: string;
    mapId: number;
    mapName: string;
    visible: boolean;
    monochrome: boolean;
    isParent: boolean;
}

export interface ControlPanelOptions {
    currentMapId: number;
    currentVersion: BuildVersion | 'latest';
    mapDataManager: MapDataManager;
    layerManager: LayerManager;
    onMapChange: (mapId: number) => void;
    onVersionChange: (version: BuildVersion | 'latest') => void;
    onLayerChange: () => void;
}

export class ControlPanel {
    private currentMapId: number;
    private currentVersion: BuildVersion | 'latest';
    private mapDataManager: MapDataManager;
    private layerManager: LayerManager;
    private onMapChange: (mapId: number) => void;
    private onVersionChange: (version: BuildVersion | 'latest') => void;
    private onLayerChange: () => void;

    private controlElement: HTMLElement;
    private mapSearchInput: HTMLInputElement;
    private mapDropdown: HTMLDivElement;
    private versionSelect: HTMLSelectElement;
    private versionWarning: HTMLDivElement | null = null;
    private layersTree: HTMLDivElement;

    private allMaps: MapInfo[] = [];
    private filteredMaps: MapInfo[] = [];
    private availableVersions: VersionInfo[] = [];
    private currentLayers: LayerDisplayInfo[] = [];
    private showDropdown = false;
    private keyboardListenerBound: ((e: KeyboardEvent) => void) | null = null;

    private mapNavTime: number = 0;
    private readonly MAP_THROTTLE_MS = 50;


    constructor(options: ControlPanelOptions) {
        this.currentMapId = options.currentMapId;
        this.currentVersion = options.currentVersion;
        this.mapDataManager = options.mapDataManager;
        this.layerManager = options.layerManager;
        this.onMapChange = options.onMapChange;
        this.onVersionChange = options.onVersionChange;
        this.onLayerChange = options.onLayerChange;

        this.controlElement = document.getElementById('map-control-panel')!;
        if (!this.controlElement) {
            throw new Error('#map-control-panel not found');
        }

        this.mapSearchInput = document.getElementById('map-search-input') as HTMLInputElement;
        if (!this.mapSearchInput) {
            throw new Error('#map-search-input not found');
        }

        this.mapDropdown = document.getElementById('map-dropdown') as HTMLDivElement;
        if (!this.mapDropdown) {
            throw new Error('#map-dropdown not found');
        }

        this.versionSelect = document.getElementById('version-selector') as HTMLSelectElement;
        if (!this.versionSelect) {
            throw new Error('#version-selector not found');
        }

        this.versionWarning = document.getElementById('version-warning') as HTMLDivElement;
        if (!this.versionWarning) {
            throw new Error('#version-warning not found');
        }

        this.layersTree = document.getElementById('layers-tree') as HTMLDivElement;
        if (!this.layersTree) {
            throw new Error('#layers-tree not found');
        }

        this.setupEventListeners();
        this.setupKeyboardShortcuts();
        this.loadMapsFromAPI();
        this.updateMapSearchText();
    }

    private setupKeyboardShortcuts(): void {
        this.keyboardListenerBound = (e: KeyboardEvent) => {
            // Don't trigger bindings while typing in a text input
            if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) {
                return;
            }

            const key = e.key.toLowerCase();

            // Q/E for version navigation
            if (key === 'e') {
                e.preventDefault();
                this.navigateVersion(-1);
            } else if (key === 'q') {
                e.preventDefault();
                this.navigateVersion(1);
            }

            // Z/C for map navigation
            else if (key === 'z') {
                e.preventDefault();
                this.navigateMap(-1);
            } else if (key === 'c') {
                e.preventDefault();
                this.navigateMap(1);
            }
        };

        document.addEventListener('keydown', this.keyboardListenerBound);
    }

    private navigateVersion(direction: number): void {
        if (this.availableVersions.length === 0) return;

        const currentIndex = this.availableVersions.findIndex((v) =>
            this.currentVersion === 'latest' ? false : v.version.equals(this.currentVersion)
        );
        if (currentIndex === -1) return;

        let newIndex = currentIndex + direction;
        newIndex = Math.max(0, Math.min(this.availableVersions.length - 1, newIndex));

        if (newIndex !== currentIndex) {
            const newVersion = this.availableVersions[newIndex];
            if (newVersion) {
                this.currentVersion = newVersion.version;
                this.versionSelect.value = newVersion.version.encodedValueString;
                this.onVersionChange(newVersion.version);
            }
        }
    }

    private navigateMap(direction: number): void {
        if (this.allMaps.length === 0) return;

        const now = performance.now();
        if (now - this.mapNavTime < this.MAP_THROTTLE_MS) return;
        this.mapNavTime = now;

        const currentIndex = this.allMaps.findIndex((m) => m.mapId === this.currentMapId);
        if (currentIndex === -1) return;

        let newIndex = currentIndex + direction;
        newIndex = Math.max(0, Math.min(this.allMaps.length - 1, newIndex));

        if (newIndex !== currentIndex) {
            const newMap = this.allMaps[newIndex];
            if (newMap) {
                this.selectMap(newMap.mapId, newMap.name);
            }
        }
    }

    private setupEventListeners(): void {
        // Map search
        this.mapSearchInput.addEventListener('focus', () => {
            this.showDropdown = true;
            this.renderDropdown();
            this.scrollToCurrentMapInDropdown();
        });

        this.mapSearchInput.addEventListener('blur', () => {
            setTimeout(() => {
                this.showDropdown = false;
                this.renderDropdown();
            }, 200);
        });

        this.mapSearchInput.addEventListener('input', (e) => {
            const target = e.target as HTMLInputElement;
            this.filterMaps(target.value);
            this.renderDropdown();
        });

        // Version selector
        this.versionSelect.addEventListener('change', (e) => {
            const target = e.target as HTMLSelectElement;
            const encodedStr = target.value;

            try {
                const version = new BuildVersion(BigInt(encodedStr));
                this.currentVersion = version;
                this.onVersionChange(version);
            } catch (error) {
                console.error('Failed to parse version from selector:', error);
            }
        });
    }

    private scrollToCurrentMapInDropdown(): void {
        // todo...
        setTimeout(() => {
            const currentMapElement = this.mapDropdown.querySelector(`[data-map-id="${this.currentMapId}"]`);
            if (currentMapElement) {
                currentMapElement.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
        }, 0);
    }

    private filterMaps(searchText: string): void {
        const search = searchText.trim().toLowerCase();

        if (!search) {
            this.filteredMaps = [...this.allMaps];
            return;
        }

        this.filteredMaps = this.allMaps
            .filter(
                (m) =>
                    m.mapId.toString().includes(search) ||
                    m.name.toLowerCase().includes(search) ||
                    m.directory.toLowerCase().includes(search)
            )
            .sort((a, b) => a.mapId - b.mapId);
    }

    private renderDropdown(): void {
        if (!this.showDropdown) {
            this.mapDropdown.style.display = 'none';
            return;
        }

        this.mapDropdown.style.display = 'block';

        if (this.filteredMaps.length === 0) {
            this.mapDropdown.innerHTML = '<div class="dropdown-item empty">No maps found</div>';
            return;
        }

        const displayMaps = this.filteredMaps.slice(0, 50);
        let html = '';

        for (const map of displayMaps) {
            html += `
                <div class="dropdown-item" data-map-id="${map.mapId}" data-map-name="${map.name}">
                    <span class="map-id">${map.mapId}</span>
                    <span class="map-name">${map.name}</span>
                </div>
            `;
        }

        if (this.filteredMaps.length > 50) {
            html += `<div class="dropdown-item info">Showing 50 of ${this.filteredMaps.length} results</div>`;
        }

        this.mapDropdown.innerHTML = html;

        const items = this.mapDropdown.querySelectorAll('.dropdown-item[data-map-id]');
        items.forEach((item) => {
            item.addEventListener('mousedown', (e) => {
                e.preventDefault();
                const mapId = parseInt(item.getAttribute('data-map-id')!);
                const mapName = item.getAttribute('data-map-name')!;
                this.selectMap(mapId, mapName);
            });
        });
    }

    private selectMap(mapId: number, mapName: string): void {
        this.currentMapId = mapId;
        this.mapSearchInput.value = `${mapId} - ${mapName}`;
        this.showDropdown = false;
        this.renderDropdown();

        this.loadVersionsForMap(mapId).then(() => {
            if (mapId === this.currentMapId) {
                this.onMapChange(mapId);
            }
        });
    }

    private updateMapSearchText(): void {
        const currentMap = this.allMaps.find((m) => m.mapId === this.currentMapId);
        this.mapSearchInput.value = currentMap
            ? `${currentMap.mapId} - ${currentMap.name}`
            : `Map ${this.currentMapId}`;
    }

    public setCurrentMap(mapId: number): void {
        this.currentMapId = mapId;
        this.updateMapSearchText();
        this.loadVersionsForMap(mapId);
    }

    public setCurrentVersion(version: BuildVersion | 'latest'): void {
        this.currentVersion = version;
        if (version === 'latest') {
            // latest = default to most recent
            if (this.availableVersions.length > 0) {
                this.versionSelect.value = this.availableVersions[0]!.version.encodedValueString;
            }
        } else {
            this.versionSelect.value = version.encodedValueString;
        }
    }

    public showVersionFallbackWarning(requestedVersion: BuildVersion | 'latest', actualVersion: BuildVersion): void {
        if (!this.versionWarning) return;

        const requestedStr = requestedVersion === 'latest' ? 'latest' : requestedVersion.toString();
        const actualStr = actualVersion.toString();

        this.versionWarning.innerHTML = `
            <span class="warning-icon">⚠️</span>
            <span class="warning-text">Not in ${requestedStr}, showing closest: ${actualStr}</span>
        `;
        this.versionWarning.classList.remove('hidden');
    }

    public hideVersionFallbackWarning(): void {
        if (!this.versionWarning) return;
        this.versionWarning.classList.add('hidden');
    }

    public setAvailableVersions(versions: VersionInfo[]): void {
        this.availableVersions = versions;

        this.versionSelect.innerHTML = '';
        for (const version of versions) {
            const option = document.createElement('option');
            option.value = version.version.encodedValueString;
            option.textContent = version.displayName;
            if (this.currentVersion !== 'latest' && version.version.equals(this.currentVersion)) {
                option.selected = true;
            }
            this.versionSelect.appendChild(option);
        }
    }

    public async loadMapsFromAPI(): Promise<void> {
        try {
            this.allMaps = await this.mapDataManager.loadMaps();
            this.filteredMaps = [...this.allMaps];
            this.updateMapSearchText();

            await this.loadVersionsForMap(this.currentMapId);
        } catch (error) {
            console.error('Failed to load maps from API:', error);
        }
    }

    private async loadVersionsForMap(mapId: number): Promise<void> {
        try {
            const versions = await this.mapDataManager.getVersionsForMap(mapId);

            // BuildVersion descending sort
            versions.sort((a, b) => b.version.compareTo(a.version));

            this.availableVersions = versions.map((v) => ({
                version: v.version,
                displayName: v.version.toString(),
                compositionHash: v.compositionHash,
                products: v.products,
            }));

            this.updateVersionSelector();
        } catch (error) {
            console.error(`Failed to load versions for map ${mapId}:`, error);
        }
    }

    private updateVersionSelector(): void {
        this.versionSelect.innerHTML = '';

        for (let i = 0; i < this.availableVersions.length; i++) {
            const versionInfo = this.availableVersions[i];
            if (!versionInfo) continue;

            const option = document.createElement('option');
            option.value = versionInfo.version.encodedValueString;

            // Check if this version has the same composition as the previous version
            let prefix = '';
            if (i > 0) {
                const prevVersion = this.availableVersions[i - 1];
                if (prevVersion && versionInfo.compositionHash === prevVersion.compositionHash) {
                    prefix = '= ';
                }
            }

            // Format: "1.2.3.456 (wow, wow_beta)"
            const productsStr = versionInfo.products.length > 0 ? ` (${versionInfo.products.join(', ')})` : '';
            const versionDisplay = `${prefix}${versionInfo.displayName}${productsStr}`;

            // Add bullet point if this is the current version
            const isCurrentVersion =
                this.currentVersion !== 'latest' && versionInfo.version.equals(this.currentVersion);
            if (isCurrentVersion) {
                option.textContent = `● ${versionDisplay}`;
                option.selected = true;
            } else {
                option.textContent = `  ${versionDisplay}`;
            }

            this.versionSelect.appendChild(option);
        }
    }

    public updateLayers(): void {
        const layers = this.layerManager.getAllLayers();
        this.currentLayers = [];

        for (const layer of layers) {
            if (!isTileLayer(layer)) continue;

            // map ID from layer ID (either "main" or "parent-{mapId}")
            let mapId: number;
            let isParent = false;
            if (layer.id === 'main') {
                mapId = this.currentMapId;
            } else if (layer.id.startsWith('parent-')) {
                mapId = parseInt(layer.id.replace('parent-', ''));
                isParent = true;
            } else {
                continue;
            }

            const mapInfo = this.allMaps.find((m) => m.mapId === mapId);
            const mapName = mapInfo?.name ?? `Map ${mapId}`;

            this.currentLayers.push({
                layerId: layer.id,
                mapId,
                mapName,
                visible: layer.visible,
                monochrome: layer.monochrome,
                isParent,
            });
        }

        // highest zIndex first photoshop-like
        this.currentLayers.sort((a, b) => (b.isParent ? -1 : 0) - (a.isParent ? -1 : 0));

        this.renderLayersTree();
    }

    private renderLayersTree(): void {
        this.layersTree.replaceChildren();

        if (this.currentLayers.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'layer-item';
            empty.innerHTML = '<span class="layer-info">No layers</span>';
            this.layersTree.appendChild(empty);
            return;
        }

        const hasParent = this.currentLayers.some((l) => l.isParent);

        for (const layer of this.currentLayers) {
            const item = this.createLayerItem(layer, hasParent);
            this.layersTree.appendChild(item);
        }
    }

    private createLayerItem(layer: LayerDisplayInfo, hasParent: boolean): HTMLElement {
        const item = document.createElement('div');
        item.className = 'layer-item';
        item.dataset['layerId'] = layer.layerId;

        // Layer info
        const info = document.createElement('span');
        info.className = 'layer-info';
        info.innerHTML = `<span class="layer-id">${layer.mapId}:</span><span class="layer-name">${layer.mapName}</span>`;
        item.appendChild(info);

        // Monochrome toggle
        if (layer.isParent && hasParent) {
            const monochromeBtn = this.createIconButton('monochrome-toggle', 'Toggle grayscale', false, () => {
                this.handleMonochromeToggle(layer.layerId);
            });
            if (layer.monochrome) monochromeBtn.classList.add('active');
            monochromeBtn.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <circle cx="12" cy="12" r="10"></circle>
                <path d="M12 2a10 10 0 0 1 0 20z" fill="currentColor"></path>
            </svg>`;
            item.appendChild(monochromeBtn);
        }

        // Visibility toggle
        const visibilityBtn = this.createIconButton('visibility-toggle', 'Toggle visibility', !layer.visible, () => {
            this.handleVisibilityToggle(layer.layerId);
        });
        visibilityBtn.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"></path>
            <circle cx="12" cy="12" r="3"></circle>
        </svg>`;
        item.appendChild(visibilityBtn);

        return item;
    }

    private createIconButton(className: string, title: string, inactive: boolean, onClick: () => void): HTMLSpanElement {
        const btn = document.createElement('span');
        btn.className = `layer-icon ${className}`;
        if (inactive) btn.classList.add('inactive');
        btn.title = title;
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            onClick();
        });
        return btn;
    }

    private handleVisibilityToggle(layerId: string): void {
        const layer = this.layerManager.getLayer(layerId);
        if (!layer) return;

        layer.visible = !layer.visible;

        const layerInfo = this.currentLayers.find((l) => l.layerId === layerId);
        if (layerInfo) {
            layerInfo.visible = layer.visible;
        }

        this.renderLayersTree();
        this.onLayerChange();
    }

    private handleMonochromeToggle(layerId: string): void {
        const layer = this.layerManager.getLayer(layerId);
        if (!layer || !isTileLayer(layer)) return;

        layer.monochrome = !layer.monochrome;

        const layerInfo = this.currentLayers.find((l) => l.layerId === layerId);
        if (layerInfo) {
            layerInfo.monochrome = layer.monochrome;
        }

        this.renderLayersTree();
        this.onLayerChange();
    }

    public dispose(): void {
        if (this.keyboardListenerBound) {
            document.removeEventListener('keydown', this.keyboardListenerBound);
            this.keyboardListenerBound = null;
        }
    }
}
