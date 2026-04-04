import type { BaseLayer, RenderContext } from './layers.js';
import type { RenderQueue, ChunkOverlayRenderCommand } from '../render-queue.js';
import type { ImpassDataDto } from '../backend-types.js';

export interface ImpassLayerOptions {
    id: string;
    visible?: boolean;
    opacity?: number;
    zIndex?: number;
}

interface ParsedImpassTile {
    x: number;
    y: number;
    bits: Uint8Array; // 32 bytes = 256 bits
}

/** Check if ADT tile chunk at (cx, cy) is impassable flagged. */
function isChunkImpass(bits: Uint8Array, cx: number, cy: number): boolean {
    // 16x16 chunks bits packed in row-major order, 1 bit per chunk, 32 bytes total
    const idx = cy * 16 + cx;
    return ((bits[Math.floor(idx / 8)] ?? 0) & (1 << (idx % 8))) !== 0;
}

/**
 * Renders impassable chunk edges...
 * Each ADT tile has 16x16 chunks, each flagged as passable or not.
 * Impassable chunks get a border along their inner edge + faint hazard fill.
 */
export class ImpassLayer implements BaseLayer {
    readonly type = 'chunk-overlay' as const;
    id: string;
    visible: boolean;
    opacity: number;
    zIndex: number;

    private tiles: ParsedImpassTile[] = [];
    private tileLookup = new Map<string, ParsedImpassTile>(); // "x,y" -> tile for cross-tile neighbour checks
    private glTextures = new Map<string, WebGLTexture>(); // "x,y" -> 16x16 texture
    private gl: WebGL2RenderingContext | null = null;

    constructor(options: ImpassLayerOptions) {
        this.id = options.id;
        this.visible = options.visible ?? false;
        this.opacity = options.opacity ?? 0.8;
        this.zIndex = options.zIndex ?? 10;
    }

    setData(data: ImpassDataDto): void {
        this.tiles = [];
        this.tileLookup.clear();
        this.disposeTextures();

        for (const [coordStr, base64Data] of Object.entries(data.tiles)) {
            const parts = coordStr.split(',').map(Number);
            const raw = atob(base64Data);
            const bits = new Uint8Array(raw.length);
            for (let i = 0; i < raw.length; i++)
                bits[i] = raw.charCodeAt(i);

            const tile = { x: parts[0]!, y: parts[1]!, bits };
            this.tiles.push(tile);
            this.tileLookup.set(coordStr, tile);
        }
    }

    /** Check if a neighbor chunk is impassable, crossing tile boundaries if needed. */
    private isNeighborImpass(tile: ParsedImpassTile, cx: number, cy: number, dx: number, dy: number): boolean {
        const nx = cx + dx, ny = cy + dy;
        if (nx >= 0 && nx < 16 && ny >= 0 && ny < 16)
            return isChunkImpass(tile.bits, nx, ny);

        // not an inter-tile check, grab the neighbour & lookup...
        const adj = this.tileLookup.get(`${tile.x + dx},${tile.y + dy}`);
        return adj !== undefined && isChunkImpass(adj.bits, (nx + 16) % 16, (ny + 16) % 16);
    }

    private getOrCreateTexture(gl: WebGL2RenderingContext, tile: ParsedImpassTile): WebGLTexture | null {
        const key = `${tile.x},${tile.y}`;
        const existing = this.glTextures.get(key);
        if (existing) return existing;

        // 16x16 1 pixel-per-chunk single channel
        //   bit 0: impass flag
        //   bit 1: right neighbor impass (sticky borders)
        //   bit 2: bottom neighbor impass
        //   bit 3: left neighbor impass
        //   bit 4: top neighbor impass
        const pixels = new Uint8Array(256);
        for (let cy = 0; cy < 16; cy++) {
            for (let cx = 0; cx < 16; cx++) {
                const impass = isChunkImpass(tile.bits, cx, cy);
                if (!impass) continue; // remain black

                let val = 1; // impass
                if (this.isNeighborImpass(tile, cx, cy, 1, 0))  val |= 2;  // right
                if (this.isNeighborImpass(tile, cx, cy, 0, 1))  val |= 4;  // bottom
                if (this.isNeighborImpass(tile, cx, cy, -1, 0)) val |= 8;  // left
                if (this.isNeighborImpass(tile, cx, cy, 0, -1)) val |= 16; // top
                pixels[cy * 16 + cx] = val;
            }
        }

        const tex = gl.createTexture();
        if (!tex)
            return null;

        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8UI, 16, 16, 0, gl.RED_INTEGER, gl.UNSIGNED_BYTE, pixels);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

        this.glTextures.set(key, tex);
        return tex;
    }

    queueRenderCommands(renderQueue: RenderQueue, _context: RenderContext): void {
        if (!this.visible || this.tiles.length === 0) return;

        for (const tile of this.tiles) {
            const command: ChunkOverlayRenderCommand = {
                type: 'chunk-overlay',
                layerId: this.id,
                zIndex: this.zIndex,
                opacity: this.opacity,
                worldX: tile.x,
                worldY: tile.y,
                tileSize: 1, // 1 tile = 1 unit in world space
                getTileTexture: (gl: WebGL2RenderingContext) => this.getOrCreateTexture(gl, tile),
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
