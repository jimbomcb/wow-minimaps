import type { MapDataManager, MapInfo } from './map-data-manager.js';
import type { LayerManager } from './layer-manager.js';
import type { TileLayer } from './layers/layers.js';
import { isTileLayer } from './layers/layers.js';
import { BuildVersion } from './build-version.js';
import { LayerType } from './backend-types.js';
import type { AreaIdDataDto } from './backend-types.js';

interface AreaNode {
    id: number;
    name: string;
    internalName: string;
    continentId: number;
    hasChunks: boolean; // true if this area has actual chunks on this map, false if only a parent reference
    children: AreaNode[];
}

export interface VersionInfo {
    version: BuildVersion;
    displayName: string;
    
    layers: (string | null)[]; // Hash of each layer, indexed by LayerType
    products: string[];
}

export interface LayerDisplayInfo {
    layerId: string;
    mapId: number;
    mapName: string;
    visible: boolean;
    monochrome: boolean;
    isParent: boolean;
    layerType: string | null; // null = main map, otherwise layer type like 'noliquid'
    partial: boolean;
}

export interface ControlPanelOptions {
    currentMapId: number;
    currentVersion: BuildVersion | 'latest';
    mapDataManager: MapDataManager;
    layerManager: LayerManager;
    onMapChange: (mapId: number) => void;
    onVersionChange: (version: BuildVersion | 'latest') => void;
    onLayerChange: () => void;
    onBaseLayerChange: (layerType: LayerType) => void;
    getAvailableBaseLayers: () => LayerType[];
    onZoneHover: (areaId: number | null) => void;
    onZoneClick: (areaId: number) => void;
}

export class ControlPanel {
    private currentMapId: number;
    private currentVersion: BuildVersion | 'latest';
    private mapDataManager: MapDataManager;
    private layerManager: LayerManager;
    private onMapChange: (mapId: number) => void;
    private onVersionChange: (version: BuildVersion | 'latest') => void;
    private onLayerChange: () => void;
    private onBaseLayerChange: (layerType: LayerType) => void;
    private getAvailableBaseLayers: () => LayerType[];
    private onZoneHover: (areaId: number | null) => void;
    private onZoneClick: (areaId: number) => void;

    private controlElement: HTMLElement;
    private mapDisplay: HTMLDivElement;
    private mapSearchInput: HTMLInputElement;
    private mapDropdown: HTMLDivElement;
    private versionDisplay: HTMLDivElement;
    private versionSearchInput: HTMLInputElement;
    private versionDropdown: HTMLDivElement;
    private versionDropdownList: HTMLDivElement;
    private versionToggleUnique: HTMLButtonElement;
    private versionFilterText: string = '';
    private versionWarning: HTMLDivElement | null = null;
    private layersTree: HTMLDivElement;
    private mapNavPrevBtn: HTMLButtonElement;
    private mapNavNextBtn: HTMLButtonElement;
    private mapNavPrevLabel: HTMLSpanElement;
    private mapNavNextLabel: HTMLSpanElement;
    private versionNavPrevBtn: HTMLButtonElement;
    private versionNavNextBtn: HTMLButtonElement;
    private versionNavPrevLabel: HTMLSpanElement;
    private versionNavNextLabel: HTMLSpanElement;
    private mapAliases: HTMLDivElement;
    private zonesSection: HTMLDivElement;
    private zonesTree: HTMLDivElement;
    private zonesTreeMode: boolean = localStorage.getItem('zones-tree-mode') !== 'flat';
    private currentAreaData: AreaIdDataDto | null = null;
    private zoneNameCount = new Map<string, number>();

    private allMaps: MapInfo[] = [];
    private filteredMaps: MapInfo[] = [];
    private availableVersions: VersionInfo[] = [];
    private currentLayers: LayerDisplayInfo[] = [];
    private showMapDropdown = false;
    private showVersionDropdown = false;
    private showUniqueOnly = true;
    private keyboardListenerBound: ((e: KeyboardEvent) => void) | null = null;

    private mapNavTime: number = 0;
    private readonly MAP_THROTTLE_MS = 50;

    // Cached version groups (rebuilt when availableVersions changes)
    private versionGroups: { layers: (string | null)[]; versions: VersionInfo[] }[] = [];


    constructor(options: ControlPanelOptions) {
        this.currentMapId = options.currentMapId;
        this.currentVersion = options.currentVersion;
        this.mapDataManager = options.mapDataManager;
        this.layerManager = options.layerManager;
        this.onMapChange = options.onMapChange;
        this.onVersionChange = options.onVersionChange;
        this.onLayerChange = options.onLayerChange;
        this.onBaseLayerChange = options.onBaseLayerChange;
        this.getAvailableBaseLayers = options.getAvailableBaseLayers;
        this.onZoneHover = options.onZoneHover;
        this.onZoneClick = options.onZoneClick;

        this.controlElement = document.getElementById('map-control-panel')!;
        if (!this.controlElement) {
            throw new Error('#map-control-panel not found');
        }

        this.mapDisplay = document.getElementById('map-display') as HTMLDivElement;
        if (!this.mapDisplay) {
            throw new Error('#map-display not found');
        }

        this.mapSearchInput = document.getElementById('map-search-input') as HTMLInputElement;
        if (!this.mapSearchInput) {
            throw new Error('#map-search-input not found');
        }

        this.mapDropdown = document.getElementById('map-dropdown') as HTMLDivElement;
        if (!this.mapDropdown) {
            throw new Error('#map-dropdown not found');
        }

        this.versionDisplay = document.getElementById('version-display') as HTMLDivElement;
        this.versionSearchInput = document.getElementById('version-search-input') as HTMLInputElement;
        this.versionDropdown = document.getElementById('version-dropdown') as HTMLDivElement;
        this.versionDropdownList = document.getElementById('version-dropdown-list') as HTMLDivElement;

        this.versionToggleUnique = document.getElementById('version-toggle-unique') as HTMLButtonElement;
        if (this.versionToggleUnique && this.showUniqueOnly) {
            this.versionToggleUnique.classList.add('active');
            const icon = this.versionToggleUnique.querySelector('.setting-icon');
            if (icon) icon.textContent = '◆';
        }

        this.versionWarning = document.getElementById('version-warning') as HTMLDivElement;
        if (!this.versionWarning) {
            throw new Error('#version-warning not found');
        }

        this.layersTree = document.getElementById('layers-tree') as HTMLDivElement;
        if (!this.layersTree) {
            throw new Error('#layers-tree not found');
        }

        this.mapNavPrevBtn = document.getElementById('map-nav-prev') as HTMLButtonElement;
        this.mapNavNextBtn = document.getElementById('map-nav-next') as HTMLButtonElement;
        this.mapNavPrevLabel = document.getElementById('map-nav-prev-label') as HTMLSpanElement;
        this.mapNavNextLabel = document.getElementById('map-nav-next-label') as HTMLSpanElement;

        this.versionNavPrevBtn = document.getElementById('version-nav-prev') as HTMLButtonElement;
        this.versionNavNextBtn = document.getElementById('version-nav-next') as HTMLButtonElement;
        this.versionNavPrevLabel = document.getElementById('version-nav-prev-label') as HTMLSpanElement;
        this.versionNavNextLabel = document.getElementById('version-nav-next-label') as HTMLSpanElement;
        this.mapAliases = document.getElementById('map-aliases') as HTMLDivElement;
        this.zonesSection = document.getElementById('zones-section') as HTMLDivElement;
        this.zonesTree = document.getElementById('zones-tree') as HTMLDivElement;

        const treeToggle = document.getElementById('zones-toggle-tree') as HTMLButtonElement;
        if (treeToggle) {
            treeToggle.classList.toggle('active', this.zonesTreeMode);
            treeToggle.addEventListener('click', () => {
                this.zonesTreeMode = !this.zonesTreeMode;
                treeToggle.classList.toggle('active', this.zonesTreeMode);
                localStorage.setItem('zones-tree-mode', this.zonesTreeMode ? 'tree' : 'flat');
                this.renderZones();
            });
        }

        document.getElementById('zones-expand-all')?.addEventListener('click', () => {
            const storageKey = `zones-collapsed-${this.currentMapId}`;
            localStorage.removeItem(storageKey);
            this.renderZones();
        });

        document.getElementById('zones-collapse-all')?.addEventListener('click', () => {
            if (!this.currentAreaData) return;
            // all area IDs that have children
            const areas = this.currentAreaData.areas;
            const hasChildren = new Set<number>();
            for (const area of Object.values(areas)) {
                if (area.ParentAreaID) {
                    hasChildren.add(area.ParentAreaID);
                }
            }
            const storageKey = `zones-collapsed-${this.currentMapId}`;
            localStorage.setItem(storageKey, JSON.stringify([...hasChildren]));
            this.renderZones();
        });

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

            // Q/E for map navigation
            if (key === 'q') {
                e.preventDefault();
                this.navigateMap(-1);
            } else if (key === 'e') {
                e.preventDefault();
                this.navigateMap(1);
            }

            // Z/C for version navigation (Z = older/prev, C = newer/next)
            else if (key === 'z') {
                e.preventDefault();
                this.navigateVersion(-1);
            } else if (key === 'c') {
                e.preventDefault();
                this.navigateVersion(1);
            }
        };

        document.addEventListener('keydown', this.keyboardListenerBound, { capture: true });
    }

    private rebuildVersionGroups(): void {
        this.versionGroups = [];
        let currentGroup: { layers: (string | null)[]; versions: VersionInfo[] } | null = null;

        for (const version of this.availableVersions) {
            const sameAsGroup = currentGroup && version.layers.every((h, i) => h === currentGroup!.layers[i]);
            if (!currentGroup || !sameAsGroup) {
                currentGroup = { layers: version.layers, versions: [version] };
                this.versionGroups.push(currentGroup);
            } else {
                currentGroup.versions.push(version);
            }
        }
    }

    private findCurrentGroupIndex(): number {
        if (this.currentVersion === 'latest') return this.versionGroups.length - 1;

        for (let i = 0; i < this.versionGroups.length; i++) {
            const group = this.versionGroups[i]!;
            if (group.versions.some(v => v.version.equals(this.currentVersion as BuildVersion))) {
                return i;
            }
        }
        return -1;
    }

    private navigateVersion(direction: number): void {
        if (this.availableVersions.length === 0) return;

        if (this.showUniqueOnly) {
            // Navigate by composition groups
            const currentGroupIndex = this.findCurrentGroupIndex();
            if (currentGroupIndex === -1) return;

            const newGroupIndex = currentGroupIndex + direction;
            if (newGroupIndex < 0 || newGroupIndex >= this.versionGroups.length) return;

            const newGroup = this.versionGroups[newGroupIndex];
            if (newGroup && newGroup.versions.length > 0) {
                const canonicalVersion = newGroup.versions[0]!;
                this.currentVersion = canonicalVersion.version;
                this.updateVersionDropdownLabel();
                this.updateVersionNavButtons();
                this.onVersionChange(canonicalVersion.version);
            }
        } else {
            // Navigate individual versions
            let currentIndex: number;
            if (this.currentVersion === 'latest') {
                currentIndex = this.availableVersions.length - 1;
            } else {
                currentIndex = this.availableVersions.findIndex((v) => v.version.equals(this.currentVersion as BuildVersion));
                if (currentIndex === -1) return;
            }

            let newIndex = currentIndex + direction;
            newIndex = Math.max(0, Math.min(this.availableVersions.length - 1, newIndex));

            if (newIndex !== currentIndex) {
                const newVersion = this.availableVersions[newIndex];
                if (newVersion) {
                    this.currentVersion = newVersion.version;
                    this.updateVersionDropdownLabel();
                    this.updateVersionNavButtons();
                    this.onVersionChange(newVersion.version);
                }
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
        // Map search - click on display to open input
        this.mapDisplay.addEventListener('click', () => {
            this.mapDisplay.style.display = 'none';
            this.mapSearchInput.style.display = 'block';
            this.mapSearchInput.focus();
            this.mapSearchInput.select();
        });

        this.mapSearchInput.addEventListener('focus', () => {
            this.showMapDropdown = true;
            this.renderMapDropdown();
            this.scrollToCurrentMapInDropdown();
        });

        this.mapSearchInput.addEventListener('blur', () => {
            setTimeout(() => {
                this.showMapDropdown = false;
                this.renderMapDropdown();
                // Switch back to display mode
                this.mapSearchInput.style.display = 'none';
                this.mapDisplay.style.display = 'block';
                this.updateMapDisplay();
            }, 200);
        });

        this.mapSearchInput.addEventListener('input', (e) => {
            const target = e.target as HTMLInputElement;
            this.filterMaps(target.value);
            this.renderMapDropdown();
        });

        this.versionDisplay.addEventListener('click', () => {
            this.versionDisplay.style.display = 'none';
            this.versionSearchInput.style.display = 'block';
            this.versionSearchInput.value = '';
            this.versionFilterText = '';
            this.versionSearchInput.focus();
        });

        this.versionSearchInput.addEventListener('focus', () => {
            this.showVersionDropdown = true;
            this.renderVersionDropdown();
            this.scrollToCurrentVersionInDropdown();
        });

        this.versionSearchInput.addEventListener('blur', () => {
            setTimeout(() => {
                this.showVersionDropdown = false;
                this.renderVersionDropdown();
                this.versionSearchInput.style.display = 'none';
                this.versionDisplay.style.display = 'block';
                this.updateVersionDropdownLabel();
            }, 200);
        });

        this.versionSearchInput.addEventListener('input', () => {
            this.versionFilterText = this.versionSearchInput.value.trim().toLowerCase();
            this.renderVersionDropdown();
        });

        // Unique toggle button
        if (this.versionToggleUnique) {
            this.versionToggleUnique.addEventListener('click', () => {
                this.showUniqueOnly = !this.showUniqueOnly;
                this.versionToggleUnique.classList.toggle('active', this.showUniqueOnly);
                const icon = this.versionToggleUnique.querySelector('.setting-icon');
                if (icon) icon.textContent = this.showUniqueOnly ? '◆' : '◇';
                this.renderVersionDropdown();
                this.updateVersionDropdownLabel();
                this.updateVersionNavButtons();
            });
        }

        // Map nav buttons
        if (this.mapNavPrevBtn) {
            this.mapNavPrevBtn.addEventListener('click', () => this.navigateMap(-1));
        }
        if (this.mapNavNextBtn) {
            this.mapNavNextBtn.addEventListener('click', () => this.navigateMap(1));
        }

        // Version nav buttons
        if (this.versionNavPrevBtn) {
            this.versionNavPrevBtn.addEventListener('click', () => this.navigateVersion(-1));
        }
        if (this.versionNavNextBtn) {
            this.versionNavNextBtn.addEventListener('click', () => this.navigateVersion(1));
        }
    }

    private scrollToCurrentMapInDropdown(): void {
        // todo...
        setTimeout(() => {
            const currentMapElement = this.mapDropdown.querySelector(`[data-map-id="${this.currentMapId}"]`);
            if (currentMapElement) {
                currentMapElement.scrollIntoView({ block: 'center' });
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

    private renderMapDropdown(): void {
        if (!this.showMapDropdown) {
            this.mapDropdown.style.display = 'none';
            return;
        }

        this.mapDropdown.style.display = 'block';

        if (this.filteredMaps.length === 0) {
            this.mapDropdown.innerHTML = '<div class="dropdown-item empty">No maps found</div>';
            return;
        }

        let html = `
            <div class="dropdown-header">
                <span class="col-id">ID</span>
                <span class="col-name">Name</span>
                <span class="col-version">First</span>
                <span class="col-version">Last</span>
                <span class="col-count-unique">Unique</span>
                <span class="col-count-total">Builds</span>
            </div>
        `;

        for (const map of this.filteredMaps) {
            const isCurrent = map.mapId === this.currentMapId;
            html += `
                <div class="dropdown-item${isCurrent ? ' current' : ''}" data-map-id="${map.mapId}" data-map-name="${map.name}">
                    <span class="col-id id-highlight">${map.mapId}</span>
                    <span class="col-name">${map.name}</span>
                    <span class="col-version">${map.first.toString()}</span>
                    <span class="col-version">${map.last.toString()}</span>
                    <span class="col-count-unique">${map.uniqueCount}</span>
                    <span class="col-count-total">${map.versionCount}</span>
                </div>
            `;
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

    private renderVersionDropdown(): void {
        if (!this.showVersionDropdown) {
            this.versionDropdown.style.display = 'none';
            return;
        }

        this.versionDropdown.style.display = 'block';

        if (this.versionGroups.length === 0) {
            this.versionDropdownList.innerHTML = '<div class="version-item">No versions available</div>';
            return;
        }

        const filter = this.versionFilterText;

        let html = `
            <div class="dropdown-header">
                <span class="col-name">Version</span>
                <span class="col-version">Products</span>
            </div>
        `;

        let visibleCount = 0;
        for (const group of this.versionGroups) {
            const isMultiple = group.versions.length > 1;

            const groupIsSelected = group.versions.some(v =>
                this.currentVersion !== 'latest' && v.version.equals(this.currentVersion as BuildVersion)
            );

            for (let i = 0; i < group.versions.length; i++) {
                const version = group.versions[i]!;
                const isFirst = i === 0;

                if (this.showUniqueOnly && !isFirst) continue;

                if (filter) {
                    const versionStr = version.displayName.toLowerCase();
                    const productsStr = version.products.join(' ').toLowerCase();
                    if (!versionStr.includes(filter) && !productsStr.includes(filter))
                        continue;
                }

                visibleCount++;

                const isSelected = this.showUniqueOnly
                    ? groupIsSelected
                    : (this.currentVersion !== 'latest' && version.version.equals(this.currentVersion as BuildVersion));

                const classes = [
                    'dropdown-item',
                    isSelected ? 'current' : '',
                    isFirst ? 'group-first' : 'group-member',
                ].filter(Boolean).join(' ');

                const productsStr = version.products.length > 0 ? version.products.join(', ') : '';

                let displayHtml: string;
                if (this.showUniqueOnly && isMultiple) {
                    displayHtml = this.getGroupDisplayRangeHtml(group);
                } else {
                    displayHtml = this.formatVersionHtml(version.displayName);
                }

                html += `
                    <div class="${classes}" data-version="${version.version.encodedValueString}">
                        <span class="col-name">${displayHtml}</span>
                        <span class="col-version">${productsStr}</span>
                    </div>
                `;
            }
        }

        if (visibleCount === 0) {
            html += '<div class="dropdown-item">No matching versions</div>';
        }

        this.versionDropdownList.innerHTML = html;

        const items = this.versionDropdownList.querySelectorAll('.dropdown-item[data-version]');
        items.forEach((item) => {
            item.addEventListener('mousedown', (e) => {
                e.preventDefault();
                const encodedStr = item.getAttribute('data-version')!;
                try {
                    const version = BuildVersion.fromEncodedString(encodedStr);
                    this.selectVersion(version);
                } catch (error) {
                    console.error('Failed to parse version:', error);
                }
            });
        });
    }

    private scrollToCurrentVersionInDropdown(): void {
        setTimeout(() => {
            const selectedItem = this.versionDropdownList.querySelector('.dropdown-item.current');
            if (selectedItem) {
                selectedItem.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
        }, 0);
    }

    private selectVersion(version: BuildVersion): void {
        this.currentVersion = version;
        this.showVersionDropdown = false;
        this.renderVersionDropdown();
        this.updateVersionDropdownLabel();
        this.updateVersionNavButtons();
        this.onVersionChange(version);
    }

    private updateVersionDropdownLabel(): void {
        if (this.availableVersions.length === 0) {
            this.versionDisplay.textContent = 'No versions';
            return;
        }

        // In unique mode show the group range, otherwise specific version
        if (this.showUniqueOnly) {
            const currentGroupIndex = this.findCurrentGroupIndex();
            if (currentGroupIndex >= 0) {
                const currentGroup = this.versionGroups[currentGroupIndex]!;
                this.versionDisplay.innerHTML = this.getGroupDisplayRangeHtml(currentGroup);
                return;
            }
        }

        if (this.currentVersion === 'latest') {
            this.versionDisplay.innerHTML = this.formatVersionHtml(this.availableVersions[this.availableVersions.length - 1]!.displayName);
        } else {
            this.versionDisplay.innerHTML = this.formatVersionHtml(this.currentVersion.toString());
        }
    }

    private selectMap(mapId: number, mapName: string): void {
        this.currentMapId = mapId;
        this.mapSearchInput.value = `${mapId} - ${mapName}`;
        this.mapSearchInput.blur();
        this.showMapDropdown = false;
        this.renderMapDropdown();
        this.updateMapNavButtons();
        this.updateMapAliases();

        this.onMapChange(mapId);
        this.loadVersionsForMap(mapId);
    }

    private updateMapSearchText(): void {
        const currentMap = this.allMaps.find((m) => m.mapId === this.currentMapId);
        this.mapSearchInput.value = currentMap
            ? `${currentMap.mapId} - ${currentMap.name}`
            : `Map ${this.currentMapId}`;
        this.updateMapDisplay();
    }

    private updateMapDisplay(): void {
        const currentMap = this.allMaps.find((m) => m.mapId === this.currentMapId);
        if (currentMap) {
            this.mapDisplay.innerHTML = `<span class="id-highlight">${currentMap.mapId}</span> ${currentMap.name}`;
        } else {
            this.mapDisplay.textContent = `Map ${this.currentMapId}`;
        }
    }

    private updateMapNavButtons(): void {
        if (!this.mapNavPrevBtn || !this.mapNavNextBtn) return;

        const currentIndex = this.allMaps.findIndex((m) => m.mapId === this.currentMapId);
        if (currentIndex > 0) {
            const prevMap = this.allMaps[currentIndex - 1]!;
            this.mapNavPrevLabel.innerHTML = `<span class="id-highlight">${prevMap.mapId}</span> ${prevMap.name}`;
            this.mapNavPrevBtn.disabled = false;
        } else {
            this.mapNavPrevLabel.textContent = '—';
            this.mapNavPrevBtn.disabled = true;
        }

        if (currentIndex >= 0 && currentIndex < this.allMaps.length - 1) {
            const nextMap = this.allMaps[currentIndex + 1]!;
            this.mapNavNextLabel.innerHTML = `<span class="id-highlight">${nextMap.mapId}</span> ${nextMap.name}`;
            this.mapNavNextBtn.disabled = false;
        } else {
            this.mapNavNextLabel.textContent = '—';
            this.mapNavNextBtn.disabled = true;
        }
    }

    private formatVersionHtml(version: string): string {
        return version;
    }

    // Tabular layout for nav buttons
    private getGroupDisplayRangeVerticalHtml(group: { layers: (string | null)[]; versions: VersionInfo[] }): string {
        if (group.versions.length === 1) {
            return this.formatVersionHtml(group.versions[0]!.displayName);
        }
        const oldest = group.versions[0]!;
        const newest = group.versions[group.versions.length - 1]!;
        return `<span class="version-range"><span class="range-row"><span class="range-label">From</span><span class="range-ver">${this.formatVersionHtml(oldest.displayName)}</span></span><span class="range-row"><span class="range-label">To</span><span class="range-ver">${this.formatVersionHtml(newest.displayName)}</span></span></span>`;
    }

    // Inline horizontal layout for dropdowns
    private getGroupDisplayRangeHtml(group: { layers: (string | null)[]; versions: VersionInfo[] }): string {
        if (group.versions.length === 1) {
            return this.formatVersionHtml(group.versions[0]!.displayName);
        }
        const oldest = group.versions[0]!;
        const newest = group.versions[group.versions.length - 1]!;
        return `${this.formatVersionHtml(oldest.displayName)} <span class="range-sep">to</span> ${this.formatVersionHtml(newest.displayName)}`;
    }

    private updateVersionNavButtons(): void {
        if (!this.versionNavPrevBtn || !this.versionNavNextBtn) return;

        // todo: expand if i want some other grouping setups, just grouping by subsequent matches for now
        if (this.showUniqueOnly) {
            const currentGroupIndex = this.findCurrentGroupIndex();

            if (currentGroupIndex > 0) {
                const prevGroup = this.versionGroups[currentGroupIndex - 1]!;
                this.versionNavPrevLabel.innerHTML = this.getGroupDisplayRangeVerticalHtml(prevGroup);
                this.versionNavPrevBtn.disabled = false;
            } else {
                this.versionNavPrevLabel.textContent = '—';
                this.versionNavPrevBtn.disabled = true;
            }

            if (currentGroupIndex >= 0 && currentGroupIndex < this.versionGroups.length - 1) {
                const nextGroup = this.versionGroups[currentGroupIndex + 1]!;
                this.versionNavNextLabel.innerHTML = this.getGroupDisplayRangeVerticalHtml(nextGroup);
                this.versionNavNextBtn.disabled = false;
            } else {
                this.versionNavNextLabel.textContent = '—';
                this.versionNavNextBtn.disabled = true;
            }
        } else {
            let currentIndex: number;
            if (this.currentVersion === 'latest') {
                currentIndex = this.availableVersions.length - 1;
            } else {
                currentIndex = this.availableVersions.findIndex((v) => v.version.equals(this.currentVersion as BuildVersion));
            }

            if (currentIndex > 0) {
                const prevVersion = this.availableVersions[currentIndex - 1]!;
                this.versionNavPrevLabel.innerHTML = this.formatVersionHtml(prevVersion.displayName);
                this.versionNavPrevBtn.disabled = false;
            } else {
                this.versionNavPrevLabel.textContent = '—';
                this.versionNavPrevBtn.disabled = true;
            }

            if (currentIndex >= 0 && currentIndex < this.availableVersions.length - 1) {
                const nextVersion = this.availableVersions[currentIndex + 1]!;
                this.versionNavNextLabel.innerHTML = this.formatVersionHtml(nextVersion.displayName);
                this.versionNavNextBtn.disabled = false;
            } else {
                this.versionNavNextLabel.textContent = '—';
                this.versionNavNextBtn.disabled = true;
            }
        }
    }

    private updateMapAliases(): void {
        if (!this.mapAliases) return;

        const currentMap = this.allMaps.find((m) => m.mapId === this.currentMapId);
        if (!currentMap || currentMap.nameHistory.size === 0) {
            this.mapAliases.innerHTML = '';
            return;
        }

        const sortedEntries = Array.from(currentMap.nameHistory.entries()).sort((a, b) => a[0].compareTo(b[0]));
        const aliasRanges = new Map<string, { start: BuildVersion; end: BuildVersion | null }[]>();
        for (let i = 0; i < sortedEntries.length; i++) {
            const [version, alias] = sortedEntries[i]!;
            if (alias === currentMap.name) continue;

            // end version (version before next name change, or null if current)
            let endVersion: BuildVersion | null = null;
            if (i < sortedEntries.length - 1) {
                endVersion = sortedEntries[i + 1]![0];
            }

            if (!aliasRanges.has(alias)) {
                aliasRanges.set(alias, []);
            }
            aliasRanges.get(alias)!.push({ start: version, end: endVersion });
        }

        if (aliasRanges.size === 0) {
            this.mapAliases.innerHTML = '';
            return;
        }

        const aliasSpans = Array.from(aliasRanges.keys()).sort().map((alias) => {
            const ranges = aliasRanges.get(alias)!;
            const rangeStrs = ranges.map((r) => {
                if (r.end === null) {
                    return `From ${r.start.toString()} until current`;
                }
                return `From ${r.start.toString()} until ${r.end.toString()}`;
            });
            const tooltip = rangeStrs.join(', ');
            return `<span class="alias-name" title="${tooltip}">${alias}</span>`;
        });

        this.mapAliases.innerHTML = `<span class="alias-label">aka:</span><span class="alias-list">${aliasSpans.join(', ')}</span>`;
    }

    public setCurrentMap(mapId: number): void {
        this.currentMapId = mapId;
        this.updateMapSearchText();
        this.updateMapNavButtons();
        this.updateMapAliases();
        this.loadVersionsForMap(mapId);
    }

    public setCurrentVersion(version: BuildVersion | 'latest'): void {
        this.currentVersion = version;

        if (this.availableVersions.length > 0) {
            this.updateVersionDropdownLabel();
            this.updateVersionNavButtons();
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
        this.rebuildVersionGroups();
        this.updateVersionDropdownLabel();
        this.updateVersionNavButtons();
    }

    public async loadMapsFromAPI(): Promise<void> {
        try {
            this.allMaps = await this.mapDataManager.loadMaps();
            this.filteredMaps = [...this.allMaps];
            this.updateMapSearchText();
            this.updateMapNavButtons();
            this.updateMapAliases();

            await this.loadVersionsForMap(this.currentMapId);
        } catch (error) {
            console.error('Failed to load maps from API:', error);
        }
    }

    private async loadVersionsForMap(mapId: number): Promise<void> {
        try {
            const versions = await this.mapDataManager.getVersionsForMap(mapId);

            // BuildVersion ascending sort (oldest first)
            versions.sort((a, b) => a.version.compareTo(b.version));

            this.availableVersions = versions.map((v) => ({
                version: v.version,
                displayName: v.version.toString(),
                layers: v.layers,
                products: v.products,
            }));

            this.rebuildVersionGroups();
            this.updateVersionDropdownLabel();
            this.updateVersionNavButtons();
        } catch (error) {
            console.error(`Failed to load versions for map ${mapId}:`, error);
        }
    }

    private static readonly LAYER_TYPE_LABELS: Record<string, string> = {
        'noliquid': 'Underwater',
        'maptexture': 'maptexture',
        'impass': 'Impassable',
    };

    public updateLayers(): void {
        const layers = this.layerManager.getAllLayers();
        this.currentLayers = [];

        // Resolve actual version for partial checks
        const resolvedVersion = this.currentVersion === 'latest'
            ? (this.availableVersions.length > 0 ? this.availableVersions[this.availableVersions.length - 1]!.version : null)
            : this.currentVersion;

        for (const layer of layers) {
            let mapId: number;
            let isParent = false;
            let layerType: string | null = null;

            if (layer.transient)
                continue;

            // Base layers are handled by the base layer toggle, not the layer list
            if (layer.id.startsWith('base-'))
                continue;

            if (layer.id.startsWith('parent-')) {
                if (!isTileLayer(layer))
                    continue;
                mapId = parseInt(layer.id.replace('parent-', ''));
                isParent = true;
            } else {
                // Layer type pattern: {layerType}-{mapId}
                const dashIdx = layer.id.lastIndexOf('-');
                if (dashIdx > 0) {
                    layerType = layer.id.substring(0, dashIdx);
                    mapId = parseInt(layer.id.substring(dashIdx + 1));
                } else {
                    continue;
                }
            }

            const mapInfo = this.allMaps.find((m) => m.mapId === mapId);
            const mapName = layerType
                ? (ControlPanel.LAYER_TYPE_LABELS[layerType] ?? layerType)
                : (mapInfo?.name ?? `Map ${mapId}`);

            // TODO: derive from cdn_missing....
            const partial = false;

            this.currentLayers.push({
                layerId: layer.id,
                mapId,
                mapName,
                visible: layer.visible,
                monochrome: isTileLayer(layer) ? layer.monochrome : false,
                isParent,
                layerType,
                partial,
            });
        }

        // Sort: parent layers at bottom, additional layers above main
        // Highest z first like photoshop.
        this.currentLayers.sort((a, b) => {
            if (a.isParent !== b.isParent) return a.isParent ? 1 : -1;
            if ((a.layerType !== null) !== (b.layerType !== null)) return a.layerType !== null ? -1 : 1;
            return 0;
        });

        this.renderLayersTree();
    }

    private renderLayersTree(): void {
        this.layersTree.replaceChildren();

        // Base layer toggle (minimap / maptexture)
        const availableBaseLayers = this.getAvailableBaseLayers();
        if (availableBaseLayers.length > 1) {
            const baseToggle = document.createElement('div');
            baseToggle.className = 'base-layer-toggle';

            const label = document.createElement('span');
            label.className = 'base-layer-label';
            label.textContent = 'Base:';
            baseToggle.appendChild(label);

            for (const lt of availableBaseLayers) {
                const btn = document.createElement('button');
                btn.className = 'base-layer-btn';
                btn.textContent = lt === LayerType.Minimap ? 'Minimap' : 'MapTexture';
                // Check if this base layer's tile layer is currently visible
                const tileLayer = this.layerManager.getLayer(`base-${LayerType[lt]}`);
                if (tileLayer?.visible) {
                    btn.classList.add('active');
                }
                btn.addEventListener('click', () => {
                    this.onBaseLayerChange(lt);
                });
                baseToggle.appendChild(btn);
            }

            this.layersTree.appendChild(baseToggle);
        }

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
        const partialBadge = layer.partial ? ' <span class="layer-partial" title="Incomplete layer — some tiles not available at archive time">!</span>' : '';
        info.innerHTML = `<span class="id-highlight">${layer.mapId}</span> <span class="layer-name">${layer.mapName}</span>${partialBadge}`;
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

    public setZones(areaData: AreaIdDataDto | null): void {
        this.currentAreaData = areaData;
        this.renderZones();
    }

    private renderZones(): void {
        if (!this.zonesSection || !this.zonesTree)
            return;

        this.zonesTree.replaceChildren();

        const areaData = this.currentAreaData;
        if (!areaData || Object.keys(areaData.areas).length === 0) {
            this.zonesSection.style.display = 'none';
            return;
        }

        this.zonesSection.style.display = '';

        const areas = areaData.areas;

        // Determine which area IDs actually have chunks on this map
        const areasWithChunks = new Set<number>();
        if (areaData.tiles) {
            for (const areaIds of Object.values(areaData.tiles)) {
                for (const id of areaIds) {
                    if (id !== 0) areasWithChunks.add(id);
                }
            }
        }

        const nodeMap = new Map<number, AreaNode>();
        for (const [idStr, area] of Object.entries(areas)) {
            const internalName = (area['ZoneName'] as string) ?? '';
            const continentId = (area['ContinentID'] as number) ?? -1;
            const hasChunks = areasWithChunks.has(area.ID);
            nodeMap.set(Number(idStr), { id: area.ID, name: area.AreaName_lang, internalName, continentId, hasChunks, children: [] });
        }

        // Track the count of the specific AreaIDs/names seen, this helps disambiguate
        // because otherwise you have just The Great Sea, The Great Sea, and.. The Great Sea that are all different underlying zones.
        this.zoneNameCount.clear();
        const seenNameIds = new Map<string, Set<number>>();
        for (const node of nodeMap.values()) {
            let ids = seenNameIds.get(node.name);
            if (!ids) {
                ids = new Set();
                seenNameIds.set(node.name, ids);
            }
            ids.add(node.id);
        }
        for (const [name, ids] of seenNameIds) {
            this.zoneNameCount.set(name, ids.size);
        }

        // Build parent-child relationships
        const roots: AreaNode[] = [];
        for (const [idStr, area] of Object.entries(areas)) {
            const node = nodeMap.get(Number(idStr))!;
            const parentNode = area.ParentAreaID ? nodeMap.get(area.ParentAreaID) : null;
            if (parentNode) {
                parentNode.children.push(node);
            } else {
                roots.push(node);
            }
        }

        const sortChildren = (nodes: AreaNode[]) => {
            nodes.sort((a, b) => a.name.localeCompare(b.name));
            for (const n of nodes) sortChildren(n.children);
        };
        sortChildren(roots);

        // Group roots by continent
        const continentGroups = new Map<number, AreaNode[]>();
        for (const root of roots) {
            const group = continentGroups.get(root.continentId);
            if (group) {
                group.push(root);
            } else {
                continentGroups.set(root.continentId, [root]);
            }
        }

        if (this.zonesTreeMode) {
            this.renderZonesGrouped(continentGroups, (groupRoots) => this.renderZonesTree(groupRoots));
        } else {
            this.renderZonesGrouped(continentGroups, (groupRoots) => {
                // Collect all descendants flat for this continent group
                const collect = (nodes: AreaNode[]): AreaNode[] => {
                    const result: AreaNode[] = [];
                    for (const n of nodes) {
                        result.push(n);
                        result.push(...collect(n.children));
                    }
                    return result;
                };
                const flat = collect(groupRoots).sort((a, b) => a.name.localeCompare(b.name));
                for (const node of flat) {
                    const row = this.createZoneRow(node);
                    let label = `<span class="zone-name">${node.name}</span>`;
                    if (node.internalName && node.internalName !== node.name) {
                        label += `<span class="zone-internal">${node.internalName}</span>`;
                    }
                    row.innerHTML = `<span class="zone-indent"></span>${label}`;
                    this.zonesTree.appendChild(row);
                }
            });
        }
    }

    private createZoneRow(node: AreaNode): HTMLDivElement {
        const row = document.createElement('div');
        row.className = 'zone-node';

        if (!node.hasChunks) {
            row.classList.add('zone-ref-only');
            row.title = `${node.name}, Internal Name: ${node.internalName}, AreaID: ${node.id}\nThis AreaID is not used directly, but it is the parent of an AreaID that is.`
        } else {
            row.title = `${node.name}, Internal Name: ${node.internalName}, AreaID: ${node.id}`;
            row.addEventListener('mouseenter', () => this.onZoneHover(node.id));
            row.addEventListener('mouseleave', () => this.onZoneHover(null));
            row.addEventListener('click', (e) => {
                if ((e.target as HTMLElement).classList.contains('zone-toggle'))
                    return;
                this.onZoneClick(node.id);
            });
        }

        return row;
    }

    private renderZonesGrouped(groups: Map<number, AreaNode[]>, renderContent: (roots: AreaNode[]) => void): void {
        // Sort continent groups by name
        const sortedGroups = [...groups.entries()].sort((a, b) => {
            const nameA = this.allMaps.find(m => m.mapId === a[0])?.name ?? `Map ${a[0]}`;
            const nameB = this.allMaps.find(m => m.mapId === b[0])?.name ?? `Map ${b[0]}`;
            return nameA.localeCompare(nameB);
        });

        for (const [continentId, groupRoots] of sortedGroups) {
            const mapInfo = this.allMaps.find(m => m.mapId === continentId);
            const name = mapInfo?.name ?? `Map ${continentId}`;

            const header = document.createElement('div');
            header.className = 'zone-continent-header';
            header.textContent = name;
            const dir = mapInfo?.directory ?? '';

            header.title = "Each AreaID has a parent 'continent' map:";
            header.title += `\nName: ${name}\nInternal Name: ${dir}, Map ${continentId}`;

            // note about why it might differ
            if (continentId !== this.currentMapId)
                header.title += `\nThese zones are associated with a different map '${name}', we are map ${this.currentMapId}.`;

            this.zonesTree.appendChild(header);

            renderContent(groupRoots);
        }
    }

    private renderZonesTree(roots: AreaNode[]): void {
        // Persisted expand/collapse state keyed by area ID
        const storageKey = `zones-collapsed-${this.currentMapId}`;
        const collapsedSet = new Set<number>(
            JSON.parse(localStorage.getItem(storageKey) ?? '[]') as number[]
        );

        const saveCollapsed = () => {
            localStorage.setItem(storageKey, JSON.stringify([...collapsedSet]));
        };

        // continuations[i] = true means ancestor at depth i has more siblings after this branch
        const renderNode = (node: AreaNode, isLast: boolean, continuations: boolean[]) => {
            const hasChildren = node.children.length > 0;
            const startExpanded = hasChildren && !collapsedSet.has(node.id);
            const depth = continuations.length;

            const row = this.createZoneRow(node);
            if (hasChildren) row.classList.add('has-children');

            let html = '';

            // Tree connectors for ancestor levels
            for (let i = 0; i < depth; i++) {
                if (i === depth - 1) {
                    // This node's own connector
                    html += `<span class="zone-line">${isLast ? '└' : '├'}</span>`;
                } else {
                    // Ancestor continuation line
                    html += `<span class="zone-line">${continuations[i] ? '│' : ' '}</span>`;
                }
            }

            if (hasChildren) {
                html += `<span class="zone-toggle">${startExpanded ? '▼' : '▶'}</span>`;
            } else if (depth === 0) {
                html += '<span class="zone-line-pad"></span>';
            }

            html += `<span class="zone-name">${node.name}</span>`;

            if (node.internalName && node.internalName !== node.name) {
                // show internal name if it's genuinely different (not just whitespace/punctuation stripped ie "Arathi's End" vs "ArathisEnd")
                // also show if multiple distinct AreaIDs share a common name to disambiguate.
                const catchRegex = /[\s':.\\-]/g;
                const isNonWhitespaceMatch = node.internalName.replaceAll(catchRegex, '').toLowerCase() === node.name.replaceAll(catchRegex, '').toLowerCase();
                const isAmbiguous = (this.zoneNameCount.get(node.name) ?? 0) > 1;

                let chosenName = '';

                if (!isNonWhitespaceMatch)
                    chosenName += node.internalName;

                if (isAmbiguous) {
                    if (chosenName !== '') chosenName += ' ';
                    chosenName += `(${node.id})`;
                }

                if (chosenName !== '')
                    html += `<span class="zone-internal">${chosenName}</span>`;
            }

            row.innerHTML = html;
            this.zonesTree.appendChild(row);

            let childContainer: HTMLDivElement | null = null;
            if (hasChildren) {
                childContainer = document.createElement('div');
                if (!startExpanded) childContainer.style.display = 'none';
                this.zonesTree.appendChild(childContainer);
            }

            if (hasChildren && childContainer) {
                const toggle = row.querySelector('.zone-toggle')!;
                toggle.addEventListener('click', () => {
                    const collapsed = childContainer!.style.display === 'none';
                    childContainer!.style.display = collapsed ? '' : 'none';
                    toggle.textContent = collapsed ? '▼' : '▶';
                    if (collapsed) {
                        collapsedSet.delete(node.id);
                    } else {
                        collapsedSet.add(node.id);
                    }
                    saveCollapsed();
                });
            }

            if (hasChildren && childContainer) {
                const savedParent = this.zonesTree;
                this.zonesTree = childContainer;
                const childConts = [...continuations, !isLast];
                for (let i = 0; i < node.children.length; i++) {
                    renderNode(node.children[i]!, i === node.children.length - 1, childConts);
                }
                this.zonesTree = savedParent;
            }
        };

        for (let i = 0; i < roots.length; i++) {
            renderNode(roots[i]!, i === roots.length - 1, []);
        }
    }

    public dispose(): void {
        if (this.keyboardListenerBound) {
            document.removeEventListener('keydown', this.keyboardListenerBound, { capture: true });
            this.keyboardListenerBound = null;
        }
    }
}
