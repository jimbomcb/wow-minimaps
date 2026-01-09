import { MapDataManager, MapInfo } from './map-data-manager.js';
import { BuildVersion } from './build-version.js';

export interface VersionInfo {
    version: BuildVersion;
    displayName: string;
    compositionHash: string;
    products: string[];
}

export interface ControlPanelOptions {
    currentMapId: number;
    currentVersion: BuildVersion | 'latest';
    mapDataManager: MapDataManager;
    onMapChange: (mapId: number) => void;
    onVersionChange: (version: BuildVersion | 'latest') => void;
}

export class ControlPanel {
    private currentMapId: number;
    private currentVersion: BuildVersion | 'latest';
    private mapDataManager: MapDataManager;
    private onMapChange: (mapId: number) => void;
    private onVersionChange: (version: BuildVersion | 'latest') => void;

    private controlElement: HTMLElement;
    private mapSearchInput: HTMLInputElement;
    private mapDropdown: HTMLDivElement;
    private versionSelect: HTMLSelectElement;
    private versionWarning: HTMLDivElement | null = null;

    private allMaps: MapInfo[] = [];
    private filteredMaps: MapInfo[] = [];
    private availableVersions: VersionInfo[] = [];
    private showDropdown = false;
    private keyboardListenerBound: ((e: KeyboardEvent) => void) | null = null;

    constructor(options: ControlPanelOptions) {
        this.currentMapId = options.currentMapId;
        this.currentVersion = options.currentVersion;
        this.mapDataManager = options.mapDataManager;
        this.onMapChange = options.onMapChange;
        this.onVersionChange = options.onVersionChange;

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

        // temp? Load versions for the selected map BEFORE triggering the change
        this.loadVersionsForMap(mapId).then(() => {
            this.onMapChange(mapId);
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

    public dispose(): void {
        if (this.keyboardListenerBound) {
            document.removeEventListener('keydown', this.keyboardListenerBound);
            this.keyboardListenerBound = null;
        }
    }
}
