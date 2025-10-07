import { RenderContext } from "./layers/layers.js";

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
}

export type RenderCommand = TileRenderCommand; // | other types... text render? lines?
export const isTileCommand = (cmd: RenderCommand): cmd is TileRenderCommand => cmd.type === 'tile';

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