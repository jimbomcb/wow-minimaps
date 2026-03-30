import type { BaseLayer, RenderContext } from './layers.js';
import type { RenderQueue, ChunkHighlightRenderCommand } from '../render-queue.js';
import type { AreaIdDataDto } from '../backend-types.js';

interface TileAreaData {
    x: number;
    y: number;
    areaIds: number[]; // 256 uint32 area IDs (one per chunk)
}

/**
 * Layer that highlights some specific area/chunks, used for things like the temporary highlight for hovered areas
 */
export class AreaHighlightLayer implements BaseLayer {
    readonly type = 'chunk-highlight' as const;
    readonly transient = true;
    id: string;
    visible: boolean = false;
    opacity: number = 1.0;
    zIndex: number;

    private tiles: TileAreaData[] = [];
    private highlightedAreaId: number | null = null;
    private glTextures = new Map<string, WebGLTexture>();
    private gl: WebGL2RenderingContext | null = null;
    private color: [number, number, number] = [0.3, 0.7, 1.0]; // temp

    constructor(id: string, zIndex: number = 20) {
        this.id = id;
        this.zIndex = zIndex;
    }

    setData(areaid: AreaIdDataDto): void {
        this.tiles = [];
        this.clearHighlight();

        for (const [coordStr, areaIds] of Object.entries(areaid.tiles)) {
            const parts = coordStr.split(',').map(Number);
            this.tiles.push({ x: parts[0]!, y: parts[1]!, areaIds });
        }
    }

    highlightArea(areaId: number): void {
        if (this.highlightedAreaId === areaId)
            return;

        this.disposeTextures();
        this.highlightedAreaId = areaId;
        this.visible = true;
    }

    clearHighlight(): void {
        this.disposeTextures();
        this.highlightedAreaId = null;
        this.visible = false;
    }

    // bounding box (in tile coords) of all chunks with this area ID or null
    getBoundsForArea(areaId: number): { minX: number; minY: number; maxX: number; maxY: number } | null {
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        let found = false;

        for (const tile of this.tiles) {
            for (let i = 0; i < 256; i++) {
                if (tile.areaIds[i] === areaId) {
                    const chunkX = tile.x + (i % 16) / 16;
                    const chunkY = tile.y + Math.floor(i / 16) / 16;
                    minX = Math.min(minX, chunkX);
                    minY = Math.min(minY, chunkY);
                    maxX = Math.max(maxX, chunkX + 1 / 16);
                    maxY = Math.max(maxY, chunkY + 1 / 16);
                    found = true;
                }
            }
        }

        return found ? { minX, minY, maxX, maxY } : null;
    }

    private getOrCreateTexture(gl: WebGL2RenderingContext, tile: TileAreaData): WebGLTexture | null {
        if (this.highlightedAreaId === null)
            return null;

        const key = `${tile.x},${tile.y}`;
        const existing = this.glTextures.get(key);
        if (existing)
            return existing;

        // 16x16 R8 mask: 255 for matching
        const pixels = new Uint8Array(256);
        let hasMatch = false;
        for (let i = 0; i < 256; i++) {
            if (tile.areaIds[i] === this.highlightedAreaId) {
                pixels[i] = 255;
                hasMatch = true;
            }
        }

        if (!hasMatch)
            return null;

        const tex = gl.createTexture();
        if (!tex)
            return null;

        this.gl = gl;
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, 16, 16, 0, gl.RED, gl.UNSIGNED_BYTE, pixels);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

        this.glTextures.set(key, tex);
        return tex;
    }

    queueRenderCommands(renderQueue: RenderQueue, _context: RenderContext): void {
        if (!this.visible || this.highlightedAreaId === null)
            return;

        for (const tile of this.tiles) {
            const command: ChunkHighlightRenderCommand = {
                type: 'chunk-highlight',
                layerId: this.id,
                zIndex: this.zIndex,
                opacity: this.opacity,
                worldX: tile.x,
                worldY: tile.y,
                tileSize: 1,
                color: this.color,
                getTileTexture: (gl) => this.getOrCreateTexture(gl, tile),
            };
            renderQueue.push(command);
        }
    }

    private disposeTextures(): void {
        if (this.gl) {
            for (const tex of this.glTextures.values())
                this.gl.deleteTexture(tex);
        }
        this.glTextures.clear();
    }

    dispose(): void {
        this.disposeTextures();
    }
}
