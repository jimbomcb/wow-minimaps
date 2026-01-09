import { CameraPosition } from './types.js';
import type { CompositionBounds } from './types.js';

// Manage camera control, input layer
export class CameraController {
    private position: CameraPosition;
    private cameraMovedCallbacks: ((camera: CameraPosition) => void)[] = [];
    private cameraReleasedCallbacks: ((camera: CameraPosition) => void)[] = [];

    private canvas: HTMLCanvasElement | null = null;
    private resizeObserver: ResizeObserver | null = null;
    private isDragging = false;
    private lastX = 0;
    private lastY = 0;
    private onResizeCallback: (() => void) | undefined = undefined;

    private isPinching = false;
    private lastPinchDistance = 0;

    private readonly zoomLevels = [
        0.03125, 0.0625, 0.125, 0.1875, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 1.0, 1.125, 1.25, 1.5, 1.75, 2.0, 2.5,
        3.0, 4.0, 5.0, 6.0, 8.0, 10.0, 12.0, 16.0, 20.0, 24.0, 32.0, 48.0, 64.0, 96.0, 128.0, 192.0, 256.0,
    ];

    constructor(initPos: CameraPosition) {
        this.position = initPos;
    }

    attachCanvas(canvas: HTMLCanvasElement, onResize?: () => void): void {
        this.canvas = canvas;
        this.onResizeCallback = onResize;
        this.setupEventHandlers();
    }

    detachCanvas(): void {
        if (this.canvas) {
            this.removeEventHandlers();
            this.canvas = null;
        }

        this.resizeObserver?.disconnect();
    }

    getPos(): CameraPosition {
        return { ...this.position };
    }

    setPos(newPos: CameraPosition): void {
        if (this.equal(this.position, newPos)) return;

        this.position = newPos;
        this.cameraMovedCallbacks.forEach((callback) => callback(this.position));
    }

    fitToBounds(bounds: CompositionBounds, marginPercent: number = 10): void {
        if (!this.canvas) {
            // no canvas?
            return;
        }

        const marginFactor = 1 + (marginPercent / 100) * 2;
        const paddedWidth = bounds.width * marginFactor;
        const paddedHeight = bounds.height * marginFactor;

        const zoomX = (paddedWidth * 512) / this.canvas.width;
        const zoomY = (paddedHeight * 512) / this.canvas.height;
        const targetZoom = Math.max(zoomX, zoomY);

        this.setPos({
            centerX: bounds.centerX,
            centerY: bounds.centerY,
            zoom: this.findNearestZoomLevel(targetZoom),
        });

        // Trigger camera released to update URL
        this.cameraReleasedCallbacks.forEach((callback) => callback(this.position));
    }

    // Find the nearest snapped zoom level (preferring to zoom out slightly if needed)
    private findNearestZoomLevel(targetZoom: number): number {
        if (this.zoomLevels.length === 0) return targetZoom;

        const fitIndex = this.zoomLevels.findIndex((level) => level >= targetZoom);

        if (fitIndex === -1) {
            // larger than all predefined levels, use the largest
            return this.zoomLevels[this.zoomLevels.length - 1]!;
        }

        return this.zoomLevels[fitIndex]!;
    }

    onCameraMoved(callback: (camera: CameraPosition) => void): void {
        this.cameraMovedCallbacks.push(callback);
    }

    onCameraReleased(callback: (camera: CameraPosition) => void): void {
        this.cameraReleasedCallbacks.push(callback);
    }

    private setupEventHandlers(): void {
        if (!this.canvas) return;

        this.resizeObserver = new ResizeObserver(() => {
            this.onResizeCallback?.();
        });

        this.resizeObserver.observe(this.canvas);

        this.canvas.tabIndex = 0;

        this.canvas.addEventListener('mousedown', this.handleMouseDown);
        this.canvas.addEventListener('wheel', this.handleWheel);
        this.canvas.addEventListener('keydown', this.handleKeyDown);

        window.addEventListener('mousemove', this.handleMouseMove);
        window.addEventListener('mouseup', this.handleMouseUp);

        // todo: validate
        this.canvas.addEventListener('touchstart', this.handleTouchStart);
        this.canvas.addEventListener('touchmove', this.handleTouchMove);
        this.canvas.addEventListener('touchend', this.handleTouchEnd);
    }

    private removeEventHandlers(): void {
        if (!this.canvas) return;

        this.canvas.removeEventListener('mousedown', this.handleMouseDown);
        this.canvas.removeEventListener('wheel', this.handleWheel);
        this.canvas.removeEventListener('keydown', this.handleKeyDown);
        this.canvas.removeEventListener('touchstart', this.handleTouchStart);
        this.canvas.removeEventListener('touchmove', this.handleTouchMove);
        this.canvas.removeEventListener('touchend', this.handleTouchEnd);

        window.removeEventListener('mousemove', this.handleMouseMove);
        window.removeEventListener('mouseup', this.handleMouseUp);
    }

    private handleMouseDown = (e: MouseEvent): void => {
        this.isDragging = true;
        this.lastX = e.clientX;
        this.lastY = e.clientY;
    };

    private handleMouseMove = (e: MouseEvent): void => {
        if (!this.isDragging) return;

        const deltaX = e.clientX - this.lastX;
        const deltaY = e.clientY - this.lastY;
        this.pan(deltaX, deltaY);
        this.lastX = e.clientX;
        this.lastY = e.clientY;
    };

    private handleMouseUp = (): void => {
        if (this.isDragging) {
            this.isDragging = false;
            this.cameraReleasedCallbacks.forEach((callback) => callback(this.position));
        }
    };

    private handleWheel = (e: WheelEvent): void => {
        e.preventDefault();

        const mousePos = this.getCanvasMousePosition(e.clientX, e.clientY);
        this.zoomAt(e.deltaY < 0, mousePos.x, mousePos.y);
        this.cameraReleasedCallbacks.forEach((callback) => callback(this.position));
    };

    private handleKeyDown = (e: KeyboardEvent): void => {
        let dx = 0,
            dy = 0;
        switch (e.key) {
            case 'ArrowUp':
                dy = 1;
                break;
            case 'ArrowDown':
                dy = -1;
                break;
            case 'ArrowLeft':
                dx = 1;
                break;
            case 'ArrowRight':
                dx = -1;
                break;
            default:
                return;
        }
        e.preventDefault();
        const amount = e.ctrlKey ? 1 : e.shiftKey ? 512 : 128;
        this.pan(dx * amount, dy * amount);
        this.cameraReleasedCallbacks.forEach((callback) => callback(this.position));
    };

    private handleTouchStart = (e: TouchEvent): void => {
        if (e.touches.length === 2) {
            this.isDragging = false;
            this.isPinching = true;
            this.lastPinchDistance = this.getTouchDistance(e.touches);
        } else if (e.touches.length === 1) {
            const touch = e.touches.item(0);
            if (!touch) return;
            this.isDragging = true;
            this.isPinching = false;
            this.lastX = touch.clientX;
            this.lastY = touch.clientY;
        }
    };

    private handleTouchMove = (e: TouchEvent): void => {
        e.preventDefault();

        if (e.touches.length === 2 && this.isPinching) {
            console.log('Pinchzoom active');
            const newDistance = this.getTouchDistance(e.touches);
            const midpoint = this.getTouchMidpoint(e.touches);
            const canvasPos = this.getCanvasMousePosition(midpoint.x, midpoint.y);

            const scale = newDistance / this.lastPinchDistance;
            const newZoom = this.position.zoom / scale;
            const clampedZoom = Math.max(
                this.zoomLevels[0]!,
                Math.min(this.zoomLevels[this.zoomLevels.length - 1]!, newZoom)
            );

            this.zoomToLevel(clampedZoom, canvasPos.x, canvasPos.y);
            this.lastPinchDistance = newDistance;
        } else if (e.touches.length === 1 && this.isDragging) {
            const touch = e.touches.item(0);
            if (!touch) return;
            const deltaX = touch.clientX - this.lastX;
            const deltaY = touch.clientY - this.lastY;

            this.pan(deltaX, deltaY);
            this.lastX = touch.clientX;
            this.lastY = touch.clientY;
        }
    };

    private handleTouchEnd = (e: TouchEvent): void => {
        if (e.touches.length === 0) {
            if (this.isDragging || this.isPinching) {
                this.isDragging = false;
                this.isPinching = false;
                this.cameraReleasedCallbacks.forEach((callback) => callback(this.position));
            }
        } else if (e.touches.length === 1 && this.isPinching) {
            // 2 touch to 1, switch to pan
            this.isPinching = false;
            this.isDragging = true;
            const touch = e.touches.item(0);
            if (touch) {
                this.lastX = touch.clientX;
                this.lastY = touch.clientY;
            }
        }
    };

    // sqrt dist between fingers (assuming 2 touching)
    private getTouchDistance(touches: TouchList): number {
        const t0 = touches.item(0)!;
        const t1 = touches.item(1)!;
        const dx = t1.clientX - t0.clientX;
        const dy = t1.clientY - t0.clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }

    // midpoint (assuming 2 touching)
    private getTouchMidpoint(touches: TouchList): { x: number; y: number } {
        const t0 = touches.item(0)!;
        const t1 = touches.item(1)!;
        return {
            x: (t0.clientX + t1.clientX) / 2,
            y: (t0.clientY + t1.clientY) / 2,
        };
    }

    private pan(deltaX: number, deltaY: number): void {
        const unitsPerPixel = this.position.zoom / 512;
        const worldDeltaX = deltaX * unitsPerPixel;
        const worldDeltaY = deltaY * unitsPerPixel;

        this.setPos({
            ...this.position,
            centerX: this.position.centerX - worldDeltaX,
            centerY: this.position.centerY - worldDeltaY,
        });
    }

    private zoomAt(zoomIn: boolean, mouseX?: number, mouseY?: number): void {
        if (this.zoomLevels.length === 0) return;

        const currentIndex = this.zoomLevels.findIndex((level) => level >= this.position.zoom);
        let targetIndex: number;
        if (zoomIn) {
            targetIndex = currentIndex > 0 ? currentIndex - 1 : 0;
        } else {
            targetIndex = currentIndex < this.zoomLevels.length - 1 ? currentIndex + 1 : this.zoomLevels.length - 1;
        }

        const targetZoom = this.zoomLevels[targetIndex]!;
        this.zoomToLevel(targetZoom, mouseX, mouseY);
    }

    private zoomToLevel(targetZoom: number, mouseX?: number, mouseY?: number): void {
        if (!this.canvas) return;

        if (mouseX !== undefined && mouseY !== undefined) {
            const worldMousePos = this.canvasToWorldPos(mouseX, mouseY);
            const zoomRatio = targetZoom / this.position.zoom;
            this.setPos({
                centerX: worldMousePos.x + (this.position.centerX - worldMousePos.x) * zoomRatio,
                centerY: worldMousePos.y + (this.position.centerY - worldMousePos.y) * zoomRatio,
                zoom: targetZoom,
            });
        } else {
            this.setPos({
                ...this.position,
                zoom: targetZoom,
            });
        }
    }

    private getCanvasMousePosition(clientX: number, clientY: number): { x: number; y: number } {
        if (!this.canvas) return { x: 0, y: 0 };
        const rect = this.canvas.getBoundingClientRect();
        return {
            x: clientX - rect.left,
            y: clientY - rect.top,
        };
    }

    private equal(a: CameraPosition, b: CameraPosition): boolean {
        const epsilon = 0.0001;
        return (
            Math.abs(a.centerX - b.centerX) < epsilon &&
            Math.abs(a.centerY - b.centerY) < epsilon &&
            Math.abs(a.zoom - b.zoom) < epsilon
        );
    }

    private canvasToWorldPos(canvasX: number, canvasY: number): { x: number; y: number } {
        if (!this.canvas) return { x: 0, y: 0 };

        const offsetX = canvasX - this.canvas.width / 2;
        const offsetY = canvasY - this.canvas.height / 2;

        const unitsPerPixel = this.position.zoom / 512;
        const worldX = this.position.centerX + offsetX * unitsPerPixel;
        const worldY = this.position.centerY + offsetY * unitsPerPixel;

        return { x: worldX, y: worldY };
    }
}
