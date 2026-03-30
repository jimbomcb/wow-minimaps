import { RenderContext } from './layers/layers.js';

export interface BaseRenderCommand {
    readonly layerId: string;
    readonly zIndex: number;
    readonly opacity: number;
}

export interface TileRenderCommand extends BaseRenderCommand {
    readonly type: 'tile';
    readonly texture: WebGLTexture;
    readonly worldX: number;
    readonly worldY: number;
    readonly lodLevel: number;
    readonly monochrome: boolean;
}

export interface ChunkOverlayRenderCommand extends BaseRenderCommand {
    readonly type: 'chunk-overlay';
    readonly worldX: number;
    readonly worldY: number;
    readonly tileSize: number;
    readonly getTileTexture: (gl: WebGL2RenderingContext) => WebGLTexture | null;
}

export interface ChunkHighlightRenderCommand extends BaseRenderCommand {
    readonly type: 'chunk-highlight';
    readonly worldX: number;
    readonly worldY: number;
    readonly tileSize: number;
    readonly color: [number, number, number];
    readonly getTileTexture: (gl: WebGL2RenderingContext) => WebGLTexture | null;
}

export type RenderCommand = TileRenderCommand | ChunkOverlayRenderCommand | ChunkHighlightRenderCommand;
export const isTileCommand = (cmd: RenderCommand): cmd is TileRenderCommand => cmd.type === 'tile';
export const isChunkOverlayCommand = (cmd: RenderCommand): cmd is ChunkOverlayRenderCommand => cmd.type === 'chunk-overlay';
export const isChunkHighlightCommand = (cmd: RenderCommand): cmd is ChunkHighlightRenderCommand => cmd.type === 'chunk-highlight';

export class RenderQueue {
    private commands: RenderCommand[] = [];

    clear(): void {
        this.commands = [];
    }

    push(command: RenderCommand): void {
        this.commands.push(command);
    }

    getCommands(): readonly RenderCommand[] {
        // Sort by layer zIndex first, then by LOD level
        return [...this.commands].sort((a, b) => {
            if (a.zIndex !== b.zIndex) {
                return a.zIndex - b.zIndex;
            }

            if (isTileCommand(a) && isTileCommand(b)) {
                if (a.lodLevel !== b.lodLevel) {
                    return b.lodLevel - a.lodLevel;
                }
            }

            return 0;
        });
    }
}
