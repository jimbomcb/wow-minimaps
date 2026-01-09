/**
 * TypeScript equivalent of Minimaps.Shared.BuildVersion
 * Responsible for converting WoW's [X].[Y].[Z].[BUILD] (i.e. 11.0.7.58046) between
 * its individual components and a single sortable BIGINT received from the API.
 * This should never exist in javascript-land as a number (errors on construction), because
 * the horrors of JS mean that it would be susceptible to float precision loss.
 * It will either be a BigInt in TS land, BIGINT in the database, or a string in transit.
 * Bit-packed to: reserved(1) | expansion(11) | major(10) | minor(10) | build(32)
 * Max:           reserved      2047          | 1023      | 1023      | int32.max
 */
export class BuildVersion {
    private readonly _value: bigint;

    private static readonly BUILD_MASK = 0xffffffffn; // 32 bits
    private static readonly MINOR_MASK = 0x3ffn; // 10 bits
    private static readonly MAJOR_MASK = 0x3ffn; // 10 bits
    private static readonly EXPANSION_MASK = 0x7ffn; // 11 bits

    private static readonly BUILD_SHIFT = 0n;
    private static readonly MINOR_SHIFT = 32n;
    private static readonly MAJOR_SHIFT = 42n;
    private static readonly EXPANSION_SHIFT = 52n;

    constructor(value: bigint | string | number) {
        if (typeof value === 'string') {
            this._value = BuildVersion.parseVersionString(value);
        } else if (typeof value === 'bigint') {
            const bigintValue = BigInt(value);
            if (bigintValue < 0n) {
                throw new Error('BuildVersion encoded value must be non-negative');
            }
            this._value = bigintValue;
        } else if (typeof value === 'number') {
            throw new Error('wrong type, either encode as BigInt or string, not number that is a float internally');
        } else {
            throw new Error('Invalid type for BuildVersion constructor');
        }
    }

    static fromComponents(expansion: number, major: number, minor: number, build: number): BuildVersion {
        if (expansion < 0 || expansion > Number(BuildVersion.EXPANSION_MASK)) {
            throw new Error(`Expansion must be between 0 and ${BuildVersion.EXPANSION_MASK}`);
        }
        if (major < 0 || major > Number(BuildVersion.MAJOR_MASK)) {
            throw new Error(`Major must be between 0 and ${BuildVersion.MAJOR_MASK}`);
        }
        if (minor < 0 || minor > Number(BuildVersion.MINOR_MASK)) {
            throw new Error(`Minor must be between 0 and ${BuildVersion.MINOR_MASK}`);
        }
        if (build < 0 || build > Number(BuildVersion.BUILD_MASK)) {
            throw new Error(`Build must be between 0 and ${BuildVersion.BUILD_MASK}`);
        }

        const value =
            (BigInt(expansion) << BuildVersion.EXPANSION_SHIFT) |
            (BigInt(major) << BuildVersion.MAJOR_SHIFT) |
            (BigInt(minor) << BuildVersion.MINOR_SHIFT) |
            (BigInt(build) << BuildVersion.BUILD_SHIFT);

        return new BuildVersion(value);
    }

    private static parseVersionString(version: string): bigint {
        if (!version || version.trim() === '') {
            throw new Error('Version string cannot be null or empty');
        }

        const parts = version.split('.');
        if (parts.length !== 4) {
            throw new Error(`Version string '${version}' must be in format 'expansion.major.minor.build'`);
        }

        const expansion = parseInt(parts[0]!, 10);
        const major = parseInt(parts[1]!, 10);
        const minor = parseInt(parts[2]!, 10);
        const build = parseInt(parts[3]!, 10);

        if (isNaN(expansion) || isNaN(major) || isNaN(minor) || isNaN(build)) {
            throw new Error(`Invalid version string '${version}'`);
        }

        return BuildVersion.fromComponents(expansion, major, minor, build)._value;
    }

    static parse(version: string): BuildVersion {
        return new BuildVersion(version);
    }

    static tryParse(version: string): BuildVersion | null {
        try {
            return BuildVersion.parse(version);
        } catch {
            return null;
        }
    }

    get expansion(): number {
        return Number((this._value >> BuildVersion.EXPANSION_SHIFT) & BuildVersion.EXPANSION_MASK);
    }

    get major(): number {
        return Number((this._value >> BuildVersion.MAJOR_SHIFT) & BuildVersion.MAJOR_MASK);
    }

    get minor(): number {
        return Number((this._value >> BuildVersion.MINOR_SHIFT) & BuildVersion.MINOR_MASK);
    }

    get build(): number {
        return Number((this._value >> BuildVersion.BUILD_SHIFT) & BuildVersion.BUILD_MASK);
    }

    get encodedValue(): bigint {
        return this._value;
    }

    get encodedValueString(): string {
        return this._value.toString();
    }

    toString(): string {
        return `${this.expansion}.${this.major}.${this.minor}.${this.build}`;
    }

    compareTo(other: BuildVersion): number {
        if (this._value < other._value) return -1;
        if (this._value > other._value) return 1;
        return 0;
    }

    equals(other: BuildVersion): boolean {
        return this._value === other._value;
    }

    isLessThan(other: BuildVersion): boolean {
        return this.compareTo(other) < 0;
    }

    isLessThanOrEqual(other: BuildVersion): boolean {
        return this.compareTo(other) <= 0;
    }

    isGreaterThan(other: BuildVersion): boolean {
        return this.compareTo(other) > 0;
    }

    isGreaterThanOrEqual(other: BuildVersion): boolean {
        return this.compareTo(other) >= 0;
    }
}
