import type { RenderQueue, RenderCommand, TileRenderCommand } from './render-queue.js';
import type { CameraPosition } from './types.js';
import type { FlashQuad, ChangeType } from './flash-overlay.js';

export class Renderer {
    private gl: WebGL2RenderingContext;
    private program: WebGLProgram;
    private gridProgram: WebGLProgram;
    private glowProgram: WebGLProgram;
    private quadBuffer: WebGLBuffer;
    private gridBuffer: WebGLBuffer;
    private chunkGridBuffer: WebGLBuffer;
    private unitQuadBuffer: WebGLBuffer;
    private positionAttribute!: number;
    private texCoordAttribute!: number;
    private transformUniform!: WebGLUniformLocation;
    private textureUniform!: WebGLUniformLocation;
    private opacityUniform!: WebGLUniformLocation;
    private monochromeUniform!: WebGLUniformLocation;
    private gridPositionAttribute!: number;
    private gridTransformUniform!: WebGLUniformLocation;
    private gridColorUniform!: WebGLUniformLocation;
    private glowPositionAttribute!: number;
    private glowTransformUniform!: WebGLUniformLocation;
    private glowColorUniform!: WebGLUniformLocation;
    private glowIntensityUniform!: WebGLUniformLocation;
    private glowRatioUniform!: WebGLUniformLocation;

    // Frame ticker for debug
    private frameTickerHue: number = 0;

    /**
     * LOD bias, multiplies zoom level before calculating LOD.
     * Mainly tunable to tweak the trnsition levels without blowing out video memory...
     * I'm seeing that transitioning directly at 1.0 bias is a bit more noticable,
     * trying 10% tradeoff
     * - Values > 1.0 delay LOD transitions, < 1.0 accelerate LOD transitions
     */
    public lodBias: number = 1.05;

    constructor(canvas: HTMLCanvasElement) {
        const gl = canvas.getContext('webgl2', { stencil: true });
        if (!gl) throw new Error('WebGL2 not supported');
        this.gl = gl;

        const displayWidth = canvas.clientWidth;
        const displayHeight = canvas.clientHeight;

        if (canvas.width !== displayWidth || canvas.height !== displayHeight) {
            canvas.width = displayWidth;
            canvas.height = displayHeight;
        }

        this.program = this.createShaderProgram();
        this.gridProgram = this.createGridShaderProgram();
        this.glowProgram = this.createGlowShaderProgram();
        this.setupGLState();
        this.quadBuffer = this.createQuadBuffer();
        this.gridBuffer = this.createGridBuffer();
        this.chunkGridBuffer = this.createChunkGridBuffer();
        this.unitQuadBuffer = this.createUnitQuadBuffer();
        this.setupAttributes();
    }

    private createShaderProgram(): WebGLProgram {
        const vertexShader = this.createShader(
            this.gl.VERTEX_SHADER,
            `#version 300 es
            in vec2 a_position;
            in vec2 a_texCoord;

            uniform mat3 u_transform;

            out vec2 v_texCoord;

            void main() {
                vec3 position = u_transform * vec3(a_position, 1.0);
                gl_Position = vec4(position.xy, 0.0, 1.0);
                v_texCoord = a_texCoord;
            }
        `
        );

        const fragmentShader = this.createShader(
            this.gl.FRAGMENT_SHADER,
            `#version 300 es
            precision highp float;

            in vec2 v_texCoord;
            uniform sampler2D u_texture;
            uniform float u_opacity;
            uniform bool u_monochrome;

            out vec4 fragColor;

            void main() {
                vec4 texColor = texture(u_texture, v_texCoord);

                if (u_monochrome) {
                    // luminance calc
                    float gray = dot(texColor.rgb, vec3(0.299, 0.587, 0.114));
                    fragColor = vec4(vec3(gray), texColor.a * u_opacity);
                } else {
                    fragColor = vec4(texColor.rgb, texColor.a * u_opacity);
                }
            }
        `
        );

        const program = this.gl.createProgram()!;
        this.gl.attachShader(program, vertexShader);
        this.gl.attachShader(program, fragmentShader);
        this.gl.linkProgram(program);

        if (!this.gl.getProgramParameter(program, this.gl.LINK_STATUS)) {
            throw new Error('Failed to link shader program');
        }

        return program;
    }

    private createShader(type: number, source: string): WebGLShader {
        const shader = this.gl.createShader(type)!;
        this.gl.shaderSource(shader, source);
        this.gl.compileShader(shader);

        if (!this.gl.getShaderParameter(shader, this.gl.COMPILE_STATUS)) {
            const info = this.gl.getShaderInfoLog(shader);
            this.gl.deleteShader(shader);
            throw new Error(`Shader compilation failed: ${info}`);
        }

        return shader;
    }

    private createGridShaderProgram(): WebGLProgram {
        const vertexShader = this.createShader(
            this.gl.VERTEX_SHADER,
            `#version 300 es
            in vec2 a_position;

            uniform mat3 u_transform;

            void main() {
                vec3 position = u_transform * vec3(a_position, 1.0);
                gl_Position = vec4(position.xy, 0.0, 1.0);
            }
        `
        );

        const fragmentShader = this.createShader(
            this.gl.FRAGMENT_SHADER,
            `#version 300 es
            precision highp float;

            uniform vec4 u_color;

            out vec4 fragColor;

            void main() {
                fragColor = u_color;
            }
        `
        );

        const program = this.gl.createProgram()!;
        this.gl.attachShader(program, vertexShader);
        this.gl.attachShader(program, fragmentShader);
        this.gl.linkProgram(program);

        if (!this.gl.getProgramParameter(program, this.gl.LINK_STATUS)) {
            throw new Error('Failed to link grid shader program');
        }

        return program;
    }

    // Used during diff flashing, stencil masked colour glow highlighting add/del/mods
    private createGlowShaderProgram(): WebGLProgram {
        const vertexShader = this.createShader(
            this.gl.VERTEX_SHADER,
            `#version 300 es
            in vec2 a_position;
            uniform mat3 u_transform;
            out vec2 v_uv;
            void main() {
                vec3 position = u_transform * vec3(a_position, 1.0);
                gl_Position = vec4(position.xy, 0.0, 1.0);
                v_uv = a_position;
            }
        `
        );

        const fragmentShader = this.createShader(
            this.gl.FRAGMENT_SHADER,
            `#version 300 es
            precision highp float;
            in vec2 v_uv;
            uniform vec4 u_color;
            uniform float u_intensity;
            uniform float u_glowRatio;

            out vec4 fragColor;

            void main() {
                // Glow quad is larger than tile. tile occupies the center region.
                // glowRatio = glowSize / glowScale, passed as uniform
                float tileMin = u_glowRatio;
                float tileMax = 1.0 - u_glowRatio;

                // Distance from tile boundary
                float dx = 0.0;
                float dy = 0.0;
                if (v_uv.x < tileMin) dx = tileMin - v_uv.x;
                else if (v_uv.x > tileMax) dx = v_uv.x - tileMax;
                if (v_uv.y < tileMin) dy = tileMin - v_uv.y;
                else if (v_uv.y > tileMax) dy = v_uv.y - tileMax;

                float dist = sqrt(dx * dx + dy * dy);
                float normalizedDist = dist / u_glowRatio;
                normalizedDist = clamp(normalizedDist, 0.0, 1.0);

                float alpha = (1.0 - normalizedDist) * u_intensity;
                alpha = pow(alpha, 1.5); // Adjust falloff curve
                fragColor = vec4(u_color.rgb, alpha * u_color.a);
            }
        `
        );

        const program = this.gl.createProgram()!;
        this.gl.attachShader(program, vertexShader);
        this.gl.attachShader(program, fragmentShader);
        this.gl.linkProgram(program);

        if (!this.gl.getProgramParameter(program, this.gl.LINK_STATUS)) {
            throw new Error('Failed to link glow shader program');
        }

        return program;
    }

    private setupGLState(): void {
        this.gl.enable(this.gl.BLEND);
        this.gl.blendFunc(this.gl.SRC_ALPHA, this.gl.ONE_MINUS_SRC_ALPHA);
        this.gl.useProgram(this.program);
        this.gl.clearColor(0.0, 0.0, 0.0, 0.0);
    }

    private createQuadBuffer(): WebGLBuffer {
        // prettier-ignore
        const vertices = new Float32Array([
            0.0, 0.0,    0.0, 0.0,  // BL/BL
            1.0, 0.0,    1.0, 0.0,  // BR/BR
            0.0, 1.0,    0.0, 1.0,  // TL/TL
            1.0, 1.0,    1.0, 1.0   // TR/TR
        ]);
        const buffer = this.gl.createBuffer()!;
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, buffer);
        this.gl.bufferData(this.gl.ARRAY_BUFFER, vertices, this.gl.STATIC_DRAW);
        return buffer;
    }

    private createGridBuffer(): WebGLBuffer {
        const gridSize = 64;
        const vertices: number[] = [];

        for (let x = 0; x <= gridSize; x++) {
            vertices.push(x, 0);
            vertices.push(x, gridSize);
        }

        for (let y = 0; y <= gridSize; y++) {
            vertices.push(0, y);
            vertices.push(gridSize, y);
        }

        const buffer = this.gl.createBuffer()!;
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, buffer);
        this.gl.bufferData(this.gl.ARRAY_BUFFER, new Float32Array(vertices), this.gl.STATIC_DRAW);

        return buffer;
    }

    private createChunkGridBuffer(): WebGLBuffer {
        const chunkSize = 1 / 16; // https://wowdev.wiki/ADT/v18 "A map tile is split up into 16x16 = 256 map chunks"
        const gridSize = 64;
        const verts: number[] = [];

        for (let x = 0; x <= gridSize * 16; x++) {
            const xPos = x * chunkSize;
            verts.push(xPos, 0);
            verts.push(xPos, gridSize);
        }

        for (let y = 0; y <= gridSize * 16; y++) {
            const yPos = y * chunkSize;
            verts.push(0, yPos);
            verts.push(gridSize, yPos);
        }

        const buffer = this.gl.createBuffer()!;
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, buffer);
        this.gl.bufferData(this.gl.ARRAY_BUFFER, new Float32Array(verts), this.gl.STATIC_DRAW);
        return buffer;
    }

    private createUnitQuadBuffer(): WebGLBuffer {
        // unit quad pos only for flash/glow overlay
        // prettier-ignore
        const vertices = new Float32Array([
            0.0, 0.0,  // BL
            1.0, 0.0,  // BR
            0.0, 1.0,  // TL
            1.0, 1.0   // TR
        ]);
        const buffer = this.gl.createBuffer()!;
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, buffer);
        this.gl.bufferData(this.gl.ARRAY_BUFFER, vertices, this.gl.STATIC_DRAW);
        return buffer;
    }

    private setupAttributes(): void {
        this.positionAttribute = this.gl.getAttribLocation(this.program, 'a_position');
        this.texCoordAttribute = this.gl.getAttribLocation(this.program, 'a_texCoord');
        this.transformUniform = this.gl.getUniformLocation(this.program, 'u_transform')!;
        this.textureUniform = this.gl.getUniformLocation(this.program, 'u_texture')!;
        this.opacityUniform = this.gl.getUniformLocation(this.program, 'u_opacity')!;
        this.monochromeUniform = this.gl.getUniformLocation(this.program, 'u_monochrome')!;

        this.gridPositionAttribute = this.gl.getAttribLocation(this.gridProgram, 'a_position');
        this.gridTransformUniform = this.gl.getUniformLocation(this.gridProgram, 'u_transform')!;
        this.gridColorUniform = this.gl.getUniformLocation(this.gridProgram, 'u_color')!;

        this.glowPositionAttribute = this.gl.getAttribLocation(this.glowProgram, 'a_position');
        this.glowTransformUniform = this.gl.getUniformLocation(this.glowProgram, 'u_transform')!;
        this.glowColorUniform = this.gl.getUniformLocation(this.glowProgram, 'u_color')!;
        this.glowIntensityUniform = this.gl.getUniformLocation(this.glowProgram, 'u_intensity')!;
        this.glowRatioUniform = this.gl.getUniformLocation(this.glowProgram, 'u_glowRatio')!;

        const vao = this.gl.createVertexArray()!;
        this.gl.bindVertexArray(vao);

        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.quadBuffer);

        this.gl.enableVertexAttribArray(this.positionAttribute);
        this.gl.vertexAttribPointer(this.positionAttribute, 2, this.gl.FLOAT, false, 16, 0);

        this.gl.enableVertexAttribArray(this.texCoordAttribute);
        this.gl.vertexAttribPointer(this.texCoordAttribute, 2, this.gl.FLOAT, false, 16, 8);
    }

    renderQueue(position: CameraPosition, renderQueue: RenderQueue): void {
        this.gl.viewport(0, 0, this.gl.canvas.width, this.gl.canvas.height);
        this.gl.clear(this.gl.COLOR_BUFFER_BIT);

        this.renderGrid(position);

        // Batch & issue commands, todo:
        // Do we care about preserving the order of render commands
        // or do we just make it implicit in z indexing...

        const commands = renderQueue.getCommands();
        const commandsByType = new Map<string, RenderCommand[]>();

        for (const command of commands) {
            if (!commandsByType.has(command.type)) {
                commandsByType.set(command.type, []);
            }
            commandsByType.get(command.type)!.push(command);
        }

        const tileCommands = (commandsByType.get('tile') as TileRenderCommand[]) || [];
        this.renderTileCommands(tileCommands, position);

        // todo: text overlay
    }

    private renderTileCommands(commands: TileRenderCommand[], position: CameraPosition): void {
        if (commands.length === 0) return;

        this.gl.useProgram(this.program);
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.quadBuffer);
        this.gl.enableVertexAttribArray(this.positionAttribute);
        this.gl.vertexAttribPointer(this.positionAttribute, 2, this.gl.FLOAT, false, 16, 0);
        this.gl.enableVertexAttribArray(this.texCoordAttribute);
        this.gl.vertexAttribPointer(this.texCoordAttribute, 2, this.gl.FLOAT, false, 16, 8);

        for (const command of commands) {
            const tileSize = Math.pow(2, command.lodLevel);
            const transform = this.createTileTransform(command.worldX, command.worldY, tileSize, position);

            this.gl.activeTexture(this.gl.TEXTURE0);
            this.gl.bindTexture(this.gl.TEXTURE_2D, command.texture);
            this.gl.uniform1i(this.textureUniform, 0);
            this.gl.uniform1f(this.opacityUniform, command.opacity);
            this.gl.uniform1i(this.monochromeUniform, command.monochrome ? 1 : 0);
            this.gl.uniformMatrix3fv(this.transformUniform, false, transform);
            this.gl.drawArrays(this.gl.TRIANGLE_STRIP, 0, 4);
        }
    }

    private createTileTransform(
        worldX: number,
        worldY: number,
        tileSize: number,
        position: CameraPosition
    ): Float32Array {
        const canvasWidth = this.gl.canvas.width;
        const canvasHeight = this.gl.canvas.height;

        const pixelsPerUnit = 512 / position.zoom;
        const screenX = (worldX - position.centerX) * pixelsPerUnit;
        const screenY = (worldY - position.centerY) * pixelsPerUnit;
        const screenTileSize = tileSize * pixelsPerUnit;

        const ndcX = (screenX * 2.0) / canvasWidth;
        const ndcY = -(screenY * 2.0) / canvasHeight;
        const ndcWidth = (screenTileSize * 2.0) / canvasWidth;
        const ndcHeight = -(screenTileSize * 2.0) / canvasHeight;

        // prettier-ignore
        return new Float32Array([
            ndcWidth, 0.0,       0.0,
            0.0,      ndcHeight, 0.0,
            ndcX,     ndcY,      1.0
        ]);
    }

    private renderGrid(position: CameraPosition): void {
        this.gl.useProgram(this.gridProgram);

        const transform = this.createGridTransform(position);
        this.gl.uniformMatrix3fv(this.gridTransformUniform, false, transform);

        // Inner 16x16 ADT chunks
        if (position.zoom < 2) {
            this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.chunkGridBuffer);
            this.gl.enableVertexAttribArray(this.gridPositionAttribute);
            this.gl.vertexAttribPointer(this.gridPositionAttribute, 2, this.gl.FLOAT, false, 0, 0);
            this.gl.uniform4f(this.gridColorUniform, 0.3, 0.3, 0.35, 0.1);

            const chunkDivisions = 64 * 16;
            const chunkLines = (chunkDivisions + 1) * 2;
            this.gl.drawArrays(this.gl.LINES, 0, chunkLines * 2);
        }

        // Main 64x64 ADT grid
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.gridBuffer);
        this.gl.enableVertexAttribArray(this.gridPositionAttribute);
        this.gl.vertexAttribPointer(this.gridPositionAttribute, 2, this.gl.FLOAT, false, 0, 0);

        this.gl.uniform4f(this.gridColorUniform, 0.3, 0.3, 0.35, 0.3);

        const gridSize = 64;
        const totalLines = (gridSize + 1) * 2;
        this.gl.drawArrays(this.gl.LINES, 0, totalLines * 2);
    }

    private createGridTransform(position: CameraPosition): Float32Array {
        const canvasWidth = this.gl.canvas.width;
        const canvasHeight = this.gl.canvas.height;
        const pixelsPerUnit = 512 / position.zoom;

        const screenX = (0 - position.centerX) * pixelsPerUnit;
        const screenY = (0 - position.centerY) * pixelsPerUnit;

        const ndcX = (screenX * 2.0) / canvasWidth;
        const ndcY = -(screenY * 2.0) / canvasHeight;
        const ndcScaleX = (pixelsPerUnit * 2.0) / canvasWidth;
        const ndcScaleY = -(pixelsPerUnit * 2.0) / canvasHeight;

        // prettier-ignore
        return new Float32Array([
            ndcScaleX, 0.0,       0.0,
            0.0,       ndcScaleY, 0.0,
            ndcX,      ndcY,      1.0
        ]);
    }

    private getGlowColor(changeType: ChangeType): [number, number, number] {
        switch (changeType) {
            case 'added':
                return [0.2, 1.0, 0.3];
            case 'modified':
                return [1.0, 0.8, 0.2];
            case 'removed':
                return [1.0, 0.3, 0.2];
        }
    }

    /**
     * Render flash overlay as outer glow around the contigous changed areas
     * Stencil buffer mask out tile interiors
     */
    renderFlashOverlay(flashes: FlashQuad[], position: CameraPosition): void {
        if (flashes.length === 0) return;

        // stencil time shitheads
        this.gl.enable(this.gl.STENCIL_TEST);
        this.gl.clear(this.gl.STENCIL_BUFFER_BIT);
        this.gl.stencilFunc(this.gl.ALWAYS, 1, 0xff);
        this.gl.stencilOp(this.gl.KEEP, this.gl.KEEP, this.gl.REPLACE);
        this.gl.colorMask(false, false, false, false);

        // push flashing tile quads into stencil
        this.gl.useProgram(this.gridProgram);
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.unitQuadBuffer);
        this.gl.enableVertexAttribArray(this.gridPositionAttribute);
        this.gl.vertexAttribPointer(this.gridPositionAttribute, 2, this.gl.FLOAT, false, 0, 0);

        for (const flash of flashes) {
            this.gl.uniformMatrix3fv(
                this.gridTransformUniform,
                false,
                this.createTileTransform(flash.x, flash.y, 1, position)
            );
            this.gl.uniform4f(this.gridColorUniform, 1.0, 1.0, 1.0, 1.0);
            this.gl.drawArrays(this.gl.TRIANGLE_STRIP, 0, 4);
        }

        // mask out tile interiors, draw glow only on outside
        this.gl.colorMask(true, true, true, true);
        this.gl.stencilFunc(this.gl.EQUAL, 0, 0xff);
        this.gl.stencilOp(this.gl.KEEP, this.gl.KEEP, this.gl.KEEP);

        this.gl.useProgram(this.glowProgram);
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.unitQuadBuffer);
        this.gl.enableVertexAttribArray(this.glowPositionAttribute);
        this.gl.vertexAttribPointer(this.glowPositionAttribute, 2, this.gl.FLOAT, false, 0, 0);

        const glowSize = 0.3 * (Math.max(1, Math.min(position.zoom, 32) / 2) * 0.5);
        const glowScale = 1.0 + 2.0 * glowSize;
        const glowRatio = glowSize / glowScale;

        for (const flash of flashes) {
            this.gl.uniformMatrix3fv(
                this.glowTransformUniform,
                false,
                this.createTileTransform(flash.x - glowSize, flash.y - glowSize, glowScale, position)
            );

            const [r, g, b] = this.getGlowColor(flash.changeType);
            this.gl.uniform4f(this.glowColorUniform, r, g, b, 1.0);
            this.gl.uniform1f(this.glowIntensityUniform, flashes[0]!.intensity);
            this.gl.uniform1f(this.glowRatioUniform, glowRatio);

            this.gl.drawArrays(this.gl.TRIANGLE_STRIP, 0, 4);
        }

        this.gl.disable(this.gl.STENCIL_TEST);
    }

    /**
     * Debug line on left edge of screen changing colour each tick
     */
    renderFrameTicker(): void {
        this.gl.useProgram(this.gridProgram);
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.unitQuadBuffer);
        this.gl.enableVertexAttribArray(this.gridPositionAttribute);
        this.gl.vertexAttribPointer(this.gridPositionAttribute, 2, this.gl.FLOAT, false, 0, 0);

        // cycle through 7 distinct colours
        this.frameTickerHue = (this.frameTickerHue % 7) + 1;
        const r = this.frameTickerHue & 1 ? 1.0 : 0.0;
        const g = this.frameTickerHue & 2 ? 1.0 : 0.0;
        const b = this.frameTickerHue & 4 ? 1.0 : 0.0;

        // prettier-ignore
        const transform = new Float32Array([
            0.02, 0.0,  0.0,   // scale x
            0.0,  2.0,  0.0,   // scale y
            -1.0, -1.0, 1.0    // translate to left edge
        ]);
        this.gl.uniformMatrix3fv(this.gridTransformUniform, false, transform);
        this.gl.uniform4f(this.gridColorUniform, r, g, b, 1.0);
        this.gl.drawArrays(this.gl.TRIANGLE_STRIP, 0, 4);
    }
}
