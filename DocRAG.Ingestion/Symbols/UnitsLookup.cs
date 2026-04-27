// UnitsLookup.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Universal canonical units lookup. Tokens matching SI base/derived
///     units, common SI-prefixed combinations, US customary units, computing
///     units, and engineering units are not symbols and should never be
///     extracted. Used as a final reject in <see cref="SymbolExtractor"/>'s
///     <c>IsAdmissible</c>, alongside <see cref="Stoplist"/>.
///
///     Library-specific overrides go through <c>LibraryProfile.LikelySymbols</c>:
///     a token in LikelySymbols passes the keep rules regardless of UnitsLookup
///     classification, so a library that uses <c>URL</c>/<c>Hz</c>/etc. as a
///     real type name can still surface them by listing them explicitly.
/// </summary>
public static class UnitsLookup
{
    /// <summary>
    ///     Returns true when the candidate matches a known unit. Case-sensitive
    ///     by default (units have canonical casing — <c>kHz</c> not <c>KHz</c>),
    ///     with one safety net: a candidate that matches case-insensitively AND
    ///     is short (≤ 4 chars) is also rejected to catch <c>RPM</c>, <c>HZ</c>,
    ///     <c>MHZ</c>, etc. that show up in spec tables in inconsistent casing.
    /// </summary>
    public static bool IsUnit(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var caseSensitiveHit = smUnits.Contains(candidate);
        var shortCaseInsensitiveHit = candidate.Length > 0
                                   && candidate.Length <= ShortUnitMaxLength
                                   && smUnitsCaseInsensitive.Contains(candidate);
        var result = caseSensitiveHit || shortCaseInsensitiveHit;
        return result;
    }

    private const int ShortUnitMaxLength = 4;

    private static readonly HashSet<string> smUnits = new(StringComparer.Ordinal)
    {
        // SI base units (case-sensitive — "M" is mega-prefix, "m" is meter)
        "m", "g", "s", "A", "K", "mol", "cd",

        // SI derived units
        "Hz", "N", "Pa", "J", "W", "C", "V", "F", "S", "Wb", "T", "H", "lm",
        "lx", "Bq", "Gy", "Sv", "kat", "rad", "sr",

        // SI prefixed length
        "fm", "pm", "nm", "um", "mm", "cm", "dm", "km", "Mm",

        // SI prefixed mass
        "fg", "pg", "ng", "ug", "mg", "kg", "Mg",

        // SI prefixed time
        "fs", "ps", "ns", "us", "ms", "ks",

        // SI prefixed current
        "fA", "pA", "nA", "uA", "mA", "kA",

        // SI prefixed voltage
        "uV", "mV", "kV", "MV",

        // SI prefixed power
        "uW", "mW", "kW", "MW", "GW",

        // SI prefixed frequency
        "kHz", "MHz", "GHz", "THz", "mHz",

        // SI prefixed resistance / conductance
        "kOhm", "MOhm", "Ohm", "ohm", "ohms", "kΩ", "MΩ", "Ω", "uS", "mS", "kS",

        // SI prefixed capacitance / inductance
        "pF", "nF", "uF", "mF", "uH", "mH",

        // SI prefixed energy
        "kJ", "MJ", "mJ", "uJ",

        // Pressure
        "kPa", "MPa", "GPa", "hPa", "mbar", "bar",

        // Temperature
        "°C", "°F", "°K",

        // Angles
        "°", "deg", "rad",

        // US customary length
        "in", "ft", "yd", "mi",

        // US customary mass
        "oz", "lb", "lbs", "ton",

        // US customary volume
        "gal", "qt", "pt",

        // US customary speed
        "mph", "fps", "knot", "knots",

        // US customary power / energy
        "hp", "BTU", "btu",

        // US customary pressure
        "psi", "psia", "psig",

        // Engineering motion / control
        "rpm", "RPM", "fps", "dps", "dpm",

        // Sound / signal
        "dB", "dBm", "dBi", "dBu", "dBV", "dBW",

        // Computing — bits / bytes (case-sensitive distinguishes b vs B)
        "b", "B", "Kb", "KB", "Mb", "MB", "Gb", "GB", "Tb", "TB", "Pb", "PB",
        "KiB", "MiB", "GiB", "TiB", "PiB",

        // Computing — bandwidth
        "bps", "Kbps", "Mbps", "Gbps", "Tbps",

        // Computing — clock / timing
        "MIPS", "FLOPS", "MFLOPS", "GFLOPS", "TFLOPS"
    };

    /// <summary>
    ///     Case-insensitive secondary table for short unit-shaped tokens that
    ///     show up in spec tables in inconsistent casing (HZ, RPM, MHZ, KHZ).
    ///     Only consulted for tokens of length &lt;= ShortUnitMaxLength so the
    ///     case-insensitive lookup does not accidentally drop legitimate
    ///     identifiers that contain unit-substrings.
    /// </summary>
    private static readonly HashSet<string> smUnitsCaseInsensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "hz", "khz", "mhz", "ghz", "thz",
        "rpm", "psi", "btu",
        "mph", "fps", "dps",
        "ohm", "ohms",
        "kb", "mb", "gb", "tb", "pb",
        "bps"
    };
}
