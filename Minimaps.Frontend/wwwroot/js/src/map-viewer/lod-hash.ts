import { Md5 } from './vendor/md5.js';

const MAX_LOD = 6;

// Sentinel bytes: 0x67 repeated 16 times - cdn_missing tiles
const CDN_MISSING_SENTINEL = new Uint8Array(16).fill(0x67);
// Build-missing / no tile: all zeros
const ZERO_BYTES = new Uint8Array(16);

// Convert a 32-char lowercase hex hash to 16 bytes
function hexToBytes(hex: string): Uint8Array {
    const bytes = new Uint8Array(16);
    for (let i = 0; i < 16; i++) {
        bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
    }
    return bytes;
}

// Convert 16 bytes to lowercase hex string
function bytesToHex(bytes: Uint8Array | Int32Array): string {
    const u8 = bytes instanceof Uint8Array ? bytes : new Uint8Array(new Int32Array(bytes).buffer);
    let hex = '';
    for (let i = 0; i < u8.length; i++) {
        hex += u8[i]!.toString(16).padStart(2, '0');
    }
    return hex;
}

export interface LodTileEntry {
    hash: string;
    worldX: number;
    worldY: number;
}

/**
 * Compute LOD tile hashes from LOD0 data, matching the server-side LodHashCalculator.
 *
 * For each LOD level (1-6), groups LOD0 tiles into 2^level blocks and computes
 * MD5(child_hash_0 || child_hash_1 || ... || child_hash_n) where each 16 byte child is one of:
 * - Present tiles use their actual content hash
 * - CDN-missing tiles use the 0x67 sentinel
 * - Absent tiles (no tile at position) use all-zero bytes
 *
 *  basically needs to be in sync with Minimaps.Shared\Types\LodHashCalculator.cs
 * 
 * @param lod0CoordToHash Map of "x,y" -> hash for LOD0 tiles
 * @param cdnMissing Set of tile hashes that are cdn-missing
 * @returns Map of lodLevel -> Map of "x,y" -> { hash, worldX, worldY }
 */
export function computeLodHashes(lod0CoordToHash: ReadonlyMap<string, string>, cdnMissing: ReadonlySet<string> | null):
    Map<number, Map<string, LodTileEntry>> {
    const result = new Map<number, Map<string, LodTileEntry>>();

    for (let level = 1; level <= MAX_LOD; level++) {
        const factor = 1 << level;
        const levelMap = new Map<string, LodTileEntry>();

        for (let lodX = 0; lodX < 64; lodX += factor) {
            for (let lodY = 0; lodY < 64; lodY += factor) {
                let hasAnyChild = false;
                const md5 = new Md5();

                for (let ty = 0; ty < factor; ty++) {
                    for (let tx = 0; tx < factor; tx++) {
                        const coord = `${lodX + tx},${lodY + ty}`;
                        const childHash = lod0CoordToHash.get(coord);

                        if (childHash !== undefined) {
                            hasAnyChild = true;
                            if (cdnMissing !== null && cdnMissing.has(childHash)) {
                                md5.appendByteArray(CDN_MISSING_SENTINEL);
                            } else {
                                md5.appendByteArray(hexToBytes(childHash));
                            }
                        } else {
                            md5.appendByteArray(ZERO_BYTES);
                        }
                    }
                }

                if (!hasAnyChild)
                    continue;

                const digest = md5.end(true);
                if (!digest)
                    continue;

                const hash = bytesToHex(digest as Int32Array);
                levelMap.set(`${lodX},${lodY}`, { hash, worldX: lodX, worldY: lodY });
            }
        }

        if (levelMap.size > 0) {
            result.set(level, levelMap);
        }
    }

    return result;
}
