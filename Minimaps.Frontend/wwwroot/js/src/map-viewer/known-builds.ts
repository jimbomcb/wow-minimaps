import { BuildVersion } from './build-version.js';

/** Notable build thresholds */
export const KnownBuilds = {
    /** My working assumption is that the MapTexture threshold was Legion onwards, because that's when
     * they did a lot of LOD focused work & distant terrain rendering */
    MapTextureIntroduced: BuildVersion.parse('7.0.0.0'),
} as const;
