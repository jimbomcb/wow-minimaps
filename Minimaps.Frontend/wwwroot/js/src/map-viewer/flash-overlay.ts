/**
 * Flash overlay for highlighting changed tiles when switching versions. State for rendering.
 */

export type ChangeType = 'added' | 'modified' | 'removed';
export interface FlashQuad {
    x: number;
    y: number;
    intensity: number;
    changeType: ChangeType;
}

interface FlashState {
    remaining: number;
    changeType: ChangeType;
}

export class FlashOverlay {
    private flashTimers: Map<string, FlashState> = new Map(); // coord -> flash state
    private flashDuration: number = 15000; // total flash duration in ms
    private holdDuration: number = 1500;
    private maxIntensity: number = 0.75
    private needsRender: boolean = false;

    triggerFlash(changes: { added: Set<string>; modified: Set<string>; removed: Set<string> }, duration: number = 15000): void {
        this.flashTimers.clear();
        this.flashDuration = duration;

        for (const coord of changes.added) {
            this.flashTimers.set(coord, { remaining: duration, changeType: 'added' });
        }
        for (const coord of changes.modified) {
            this.flashTimers.set(coord, { remaining: duration, changeType: 'modified' });
        }
        for (const coord of changes.removed) {
            this.flashTimers.set(coord, { remaining: duration, changeType: 'removed' });
        }

        this.needsRender = this.flashTimers.size > 0;
    }

    clear(): void {
        this.flashTimers.clear();
        this.needsRender = false;
    }

    update(deltaTime: number): boolean {
        if (this.flashTimers.size === 0) {
            this.needsRender = false;
            return false;
        }

        const toRemove: string[] = [];
        for (const [coord, state] of this.flashTimers) {
            const newRemaining = state.remaining - deltaTime;
            if (newRemaining <= 0) {
                toRemove.push(coord);
            } else {
                state.remaining = newRemaining;
            }
        }

        for (const coord of toRemove) {
            this.flashTimers.delete(coord);
        }

        this.needsRender = this.flashTimers.size > 0;
        return this.needsRender;
    }

    getActiveFlashes(): FlashQuad[] {
        const flashes: FlashQuad[] = [];
        const fadeDuration = this.flashDuration - this.holdDuration;

        for (const [coord, state] of this.flashTimers) {
            const [x, y] = coord.split(',').map(Number);
            if (x !== undefined && y !== undefined && !isNaN(x) && !isNaN(y)) {
                let intensity: number;

                if (state.remaining > fadeDuration) {
                    // Still in hold period - stay at max
                    intensity = this.maxIntensity;
                } else {
                    // Fading period - ease out from max to 0
                    const t = state.remaining / fadeDuration;
                    intensity = t * t * this.maxIntensity; // quadratic ease-out
                }

                flashes.push({ x, y, intensity, changeType: state.changeType });
            }
        }

        return flashes;
    }

    hasActiveFlashes(): boolean {
        return this.needsRender;
    }

    get count(): number {
        return this.flashTimers.size;
    }
}
