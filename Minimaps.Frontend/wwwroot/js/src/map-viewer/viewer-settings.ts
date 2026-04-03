import { LayerType } from './backend-types.js';

/**
 * URL-persisted map viewer settings
 * Encoded as a compact base36 (0-9A-Z) integer in `?s=` query param.
 * Used for things we want to persist when sharing the URL, ie layer visiblity.
 *
 * Bit layout (LSB first):
 *   bit 0: base layer preference (0=minimap, 1=maptexture)
 *   gotta decide how tight we want to bitpack this, but compatibility will be more important.
 */
export class ViewerSettings {
    private bits: number;

    private constructor(bits: number) {
        this.bits = bits;
    }

    /** Parse from current URL, defaults if no param present. */
    static fromURL(): ViewerSettings {
        const params = new URLSearchParams(window.location.search);
        const raw = params.get('data');
        if (raw === null)
            return new ViewerSettings(0);

        const parsed = parseInt(raw, 36);
        return new ViewerSettings(Number.isNaN(parsed) ? 0 : parsed);
    }

    get preferredBaseLayer(): LayerType {
        return (this.bits & 1) === 1 ? LayerType.MapTexture : LayerType.Minimap;
    }

    set preferredBaseLayer(value: LayerType) {
        if (value === LayerType.MapTexture) {
            this.bits |= 1;
        } else {
            this.bits &= ~1;
        }
    }

    /** Write current settings into URL */
    applyToURL(url: URL): void {
        if (this.bits === 0) {
            url.searchParams.delete('data');
        } else {
            url.searchParams.set('data', this.bits.toString(36));
        }
    }

    /** Push to the browser URL without navigation */
    updateBrowserURL(): void {
        const url = new URL(window.location.href);
        this.applyToURL(url);
        window.history.replaceState({}, '', url.toString());
    }
}
