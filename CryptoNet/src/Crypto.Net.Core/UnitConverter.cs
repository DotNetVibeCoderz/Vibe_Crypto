using System.Numerics;

namespace Crypto.Net.Core;

/// <summary>
/// Exact conversions between display units and base units. All methods throw
/// <see cref="FormatException"/> instead of silently rounding when precision would be lost.
/// </summary>
public static class UnitConverter
{
    /// <summary>Known unit names and their decimals relative to the base unit.</summary>
    public static IReadOnlyDictionary<string, (string Family, int Decimals)> Units { get; } =
        new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["wei"] = ("evm", 0), ["kwei"] = ("evm", 3), ["mwei"] = ("evm", 6), ["gwei"] = ("evm", 9),
            ["szabo"] = ("evm", 12), ["finney"] = ("evm", 15), ["ether"] = ("evm", 18), ["eth"] = ("evm", 18),
            ["sat"] = ("btc", 0), ["sats"] = ("btc", 0), ["satoshi"] = ("btc", 0), ["bits"] = ("btc", 2),
            ["mbtc"] = ("btc", 5), ["btc"] = ("btc", 8),
            ["lamport"] = ("sol", 0), ["lamports"] = ("sol", 0), ["sol"] = ("sol", 9),
            ["planck"] = ("dot", 0), ["dot"] = ("dot", 10),
            ["uatom"] = ("atom", 0), ["atom"] = ("atom", 6),
        };

    /// <summary>Converts a decimal display amount into base units for a token with <paramref name="decimals"/>.</summary>
    public static BigInteger ToBaseUnits(decimal amount, int decimals) => Amount.FromDecimal(amount, decimals).BaseUnits;

    /// <summary>Converts base units into an exact display string.</summary>
    public static string ToDisplay(BigInteger baseUnits, int decimals) => new Amount(baseUnits, decimals).ToString();

    /// <summary>Converts between any two units of the same family, e.g. <c>Convert("1.5", "ether", "gwei")</c>.</summary>
    public static string Convert(string value, string fromUnit, string toUnit)
    {
        if (!Units.TryGetValue(fromUnit, out var from)) throw new ArgumentException($"Unknown unit '{fromUnit}'", nameof(fromUnit));
        if (!Units.TryGetValue(toUnit, out var to)) throw new ArgumentException($"Unknown unit '{toUnit}'", nameof(toUnit));
        if (from.Family != to.Family) throw new ArgumentException($"Cannot convert {fromUnit} to {toUnit}: different assets");

        BigInteger baseUnits = Amount.Parse(value, from.Decimals).BaseUnits;
        return new Amount(baseUnits, to.Decimals).ToString();
    }

    #region Convenience

    public static BigInteger EtherToWei(decimal ether) => ToBaseUnits(ether, 18);
    public static decimal WeiToEther(BigInteger wei) => new Amount(wei, 18).ToDecimal();
    public static BigInteger GweiToWei(decimal gwei) => ToBaseUnits(gwei, 9);
    public static decimal WeiToGwei(BigInteger wei) => new Amount(wei, 9).ToDecimal();

    public static long BtcToSatoshi(decimal btc) => (long)ToBaseUnits(btc, 8);
    public static decimal SatoshiToBtc(long satoshi) => new Amount(satoshi, 8).ToDecimal();

    public static ulong SolToLamports(decimal sol) => (ulong)ToBaseUnits(sol, 9);
    public static decimal LamportsToSol(ulong lamports) => new Amount(lamports, 9).ToDecimal();

    public static BigInteger DotToPlanck(decimal dot) => ToBaseUnits(dot, 10);
    public static decimal PlanckToDot(BigInteger planck) => new Amount(planck, 10).ToDecimal();

    public static BigInteger AtomToMicroAtom(decimal atom) => ToBaseUnits(atom, 6);
    public static decimal MicroAtomToAtom(BigInteger uatom) => new Amount(uatom, 6).ToDecimal();

    #endregion
}
