import { MapViewport } from "./types.js";

export class Renderer {
    private gl: WebGL2RenderingContext;
    private program: WebGLProgram;
    private gridProgram: WebGLProgram;
    private quadBuffer: WebGLBuffer;
    private gridBuffer: WebGLBuffer;
    private positionAttribute: number;
    private texCoordAttribute: number;
    private transformUniform: WebGLUniformLocation;
    private textureUniform: WebGLUniformLocation;
    private opacityUniform: WebGLUniformLocation;
    private gridPositionAttribute: number;
    private gridTransformUniform: WebGLUniformLocation;
    private gridColorUniform: WebGLUniformLocation;
    
    constructor(canvas: HTMLCanvasElement) {
        const gl = canvas.getContext('webgl2');
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
        this.setupGLState();
        this.quadBuffer = this.createQuadBuffer();
        this.gridBuffer = this.createGridBuffer();
        this.setupAttributes();
    }

    private createShaderProgram(): WebGLProgram {
        const vertexShader = this.createShader(this.gl.VERTEX_SHADER, `#version 300 es
            in vec2 a_position;
            in vec2 a_texCoord;
            
            uniform mat3 u_transform;
            
            out vec2 v_texCoord;
            
            void main() {
                vec3 position = u_transform * vec3(a_position, 1.0);
                gl_Position = vec4(position.xy, 0.0, 1.0);
                v_texCoord = a_texCoord;
            }
        `);

        const fragmentShader = this.createShader(this.gl.FRAGMENT_SHADER, `#version 300 es
            precision highp float;
            
            in vec2 v_texCoord;
            uniform sampler2D u_texture;
            uniform float u_opacity;
            
            out vec4 fragColor;
            
            void main() {
                vec4 texColor = texture(u_texture, v_texCoord);
                fragColor = vec4(texColor.rgb, texColor.a * u_opacity);
            }
        `);

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
        const vertexShader = this.createShader(this.gl.VERTEX_SHADER, `#version 300 es
            in vec2 a_position;
            
            uniform mat3 u_transform;
            
            void main() {
                vec3 position = u_transform * vec3(a_position, 1.0);
                gl_Position = vec4(position.xy, 0.0, 1.0);
            }
        `);

        const fragmentShader = this.createShader(this.gl.FRAGMENT_SHADER, `#version 300 es
            precision highp float;
            
            uniform vec4 u_color;
            
            out vec4 fragColor;
            
            void main() {
                fragColor = u_color;
            }
        `);

        const program = this.gl.createProgram()!;
        this.gl.attachShader(program, vertexShader);
        this.gl.attachShader(program, fragmentShader);
        this.gl.linkProgram(program);

        if (!this.gl.getProgramParameter(program, this.gl.LINK_STATUS)) {
            throw new Error('Failed to link grid shader program');
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
        const vertices = new Float32Array([
            0.0, 0.0,    0.0, 1.0,
            1.0, 0.0,    1.0, 1.0,
            0.0, 1.0,    0.0, 0.0,
            1.0, 1.0,    1.0, 0.0
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

    private setupAttributes(): void {
        this.positionAttribute = this.gl.getAttribLocation(this.program, 'a_position');
        this.texCoordAttribute = this.gl.getAttribLocation(this.program, 'a_texCoord');
        this.transformUniform = this.gl.getUniformLocation(this.program, 'u_transform')!;
        this.textureUniform = this.gl.getUniformLocation(this.program, 'u_texture')!;
        this.opacityUniform = this.gl.getUniformLocation(this.program, 'u_opacity')!;

        this.gridPositionAttribute = this.gl.getAttribLocation(this.gridProgram, 'a_position');
        this.gridTransformUniform = this.gl.getUniformLocation(this.gridProgram, 'u_transform')!;
        this.gridColorUniform = this.gl.getUniformLocation(this.gridProgram, 'u_color')!;

        const vao = this.gl.createVertexArray()!;
        this.gl.bindVertexArray(vao);
        
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.quadBuffer);
        
        this.gl.enableVertexAttribArray(this.positionAttribute);
        this.gl.vertexAttribPointer(this.positionAttribute, 2, this.gl.FLOAT, false, 16, 0);
        
        this.gl.enableVertexAttribArray(this.texCoordAttribute);
        this.gl.vertexAttribPointer(this.texCoordAttribute, 2, this.gl.FLOAT, false, 16, 8);
    }

    render(viewport: MapViewport, loadedTiles?: Array<{ tileKey: string, texture: WebGLTexture, x: number, y: number, zoom: number }>): void {
        this.gl.viewport(0, 0, this.gl.canvas.width, this.gl.canvas.height);
        this.gl.clear(this.gl.COLOR_BUFFER_BIT);
        
        this.renderGrid(viewport);
        
        if (loadedTiles && loadedTiles.length > 0) {
            this.renderLoadedTiles(loadedTiles, viewport);
        }
    }

    private renderLoadedTiles(loadedTiles: Array<{ tileKey: string, texture: WebGLTexture, x: number, y: number, zoom: number }>, viewport: MapViewport): void {
        this.gl.useProgram(this.program);
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.quadBuffer);
        this.gl.enableVertexAttribArray(this.positionAttribute);
        this.gl.vertexAttribPointer(this.positionAttribute, 2, this.gl.FLOAT, false, 16, 0);
        this.gl.enableVertexAttribArray(this.texCoordAttribute);
        this.gl.vertexAttribPointer(this.texCoordAttribute, 2, this.gl.FLOAT, false, 16, 8);
        
        for (const tileData of loadedTiles) {
            this.renderTileWithTexture(tileData.x, tileData.y, tileData.texture, viewport);
        }
    }

    private renderTileWithTexture(x: number, y: number, texture: WebGLTexture, viewport: MapViewport): void {
        const transform = this.createTileTransform(
            x, y, 1.0, viewport
        );
        
        this.gl.activeTexture(this.gl.TEXTURE0);
        this.gl.bindTexture(this.gl.TEXTURE_2D, texture);
        this.gl.uniform1i(this.textureUniform, 0);
        this.gl.uniform1f(this.opacityUniform, 1.0);
        this.gl.uniformMatrix3fv(this.transformUniform, false, transform);
        this.gl.drawArrays(this.gl.TRIANGLE_STRIP, 0, 4);
    }

    private createTileTransform(worldX: number, worldY: number, tileSize: number, viewport: MapViewport): Float32Array {
        const canvasWidth = this.gl.canvas.width;
        const canvasHeight = this.gl.canvas.height;
        
        const pixelsPerUnit = 256 / viewport.altitude;
        
        const screenX = (worldX - viewport.centerX) * pixelsPerUnit;
        const screenY = (worldY - viewport.centerY) * pixelsPerUnit;
        const screenTileSize = tileSize * pixelsPerUnit;
        
        const ndcX = (screenX * 2.0) / canvasWidth;
        const ndcY = -(screenY * 2.0) / canvasHeight;
        const ndcWidth = (screenTileSize * 2.0) / canvasWidth;
        const ndcHeight = (screenTileSize * 2.0) / canvasHeight;
        
        return new Float32Array([
            ndcWidth, 0.0,       0.0,
            0.0,      ndcHeight, 0.0,
            ndcX,     ndcY,      1.0
        ]);
    }

    private renderGrid(viewport: MapViewport): void {
        this.gl.useProgram(this.gridProgram);
        
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.gridBuffer);
        this.gl.enableVertexAttribArray(this.gridPositionAttribute);
        this.gl.vertexAttribPointer(this.gridPositionAttribute, 2, this.gl.FLOAT, false, 0, 0);
        
        const transform = this.createGridTransform(viewport);
        this.gl.uniformMatrix3fv(this.gridTransformUniform, false, transform);
        
        this.gl.uniform4f(this.gridColorUniform, 0.3, 0.3, 0.35, 0.3);
        
        const gridSize = 64;
        const totalLines = (gridSize + 1) * 2;
        this.gl.drawArrays(this.gl.LINES, 0, totalLines * 2);
    }

    private createGridTransform(viewport: MapViewport): Float32Array {
        const canvasWidth = this.gl.canvas.width;
        const canvasHeight = this.gl.canvas.height;
        
        const pixelsPerUnit = 256 / viewport.altitude;
        
        const screenX = (0 - viewport.centerX) * pixelsPerUnit;
        const screenY = (0 - viewport.centerY) * pixelsPerUnit;
        
        const ndcX = (screenX * 2.0) / canvasWidth;
        const ndcY = -(screenY * 2.0) / canvasHeight;
        const ndcScaleX = (pixelsPerUnit * 2.0) / canvasWidth;
        const ndcScaleY = (pixelsPerUnit * 2.0) / canvasHeight;
        
        return new Float32Array([
            ndcScaleX, 0.0,       0.0,
            0.0,       ndcScaleY, 0.0,
            ndcX,      ndcY,      1.0
        ]);
    }

    createTileTexture(image: HTMLImageElement): WebGLTexture {
        const texture = this.gl.createTexture()!;
        this.gl.bindTexture(this.gl.TEXTURE_2D, texture);
        
        this.gl.texImage2D(this.gl.TEXTURE_2D, 0, this.gl.RGBA, this.gl.RGBA, this.gl.UNSIGNED_BYTE, image);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MIN_FILTER, this.gl.NEAREST);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MAG_FILTER, this.gl.NEAREST);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_S, this.gl.CLAMP_TO_EDGE);
        this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_T, this.gl.CLAMP_TO_EDGE);
        
        return texture;
    }
}