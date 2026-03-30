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
        this.disposeTextures();

        for (const [coordStr, base64Data] of Object.entries(data.tiles)) {
            const parts = coordStr.split(',').map(Number);
            const raw = atob(base64Data);
            const bits = new Uint8Array(raw.length);
            for (let i = 0; i < raw.length; i++)
                bits[i] = raw.charCodeAt(i);

            this.tiles.push({ x: parts[0]!, y: parts[1]!, bits });
        }
    }

    private getOrCreateTexture(gl: WebGL2RenderingContext, tile: ParsedImpassTile): WebGLTexture | null {
        const key = `${tile.x},${tile.y}`;
        const existing = this.glTextures.get(key);
        if (existing) return existing;

        // unpack 256 bits into a 16x16 LUMINANCE texture
        const pixels = new Uint8Array(256);
        for (let i = 0; i < 256; i++) {
            const byteIdx = Math.floor(i / 8);
            const bitIdx = i % 8;
            pixels[i] = ((tile.bits[byteIdx] ?? 0) & (1 << bitIdx)) ? 255 : 0;
        }

        const tex = gl.createTexture();
        if (!tex)
            return null;

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
