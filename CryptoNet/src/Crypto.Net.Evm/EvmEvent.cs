using Crypto.Net.Core;

namespace Crypto.Net.Evm;

/// <summary>A parameter of a Solidity event.</summary>
public sealed record EvmEventParameter(string Name, AbiType Type, bool Indexed);

/// <summary>A decoded log: event name, parameters by name (in declaration order) and the source log.</summary>
public sealed record EvmDecodedEvent(string Name, IReadOnlyDictionary<string, object?> Parameters, EvmLog Log)
{
    public object? this[string parameter] => Parameters.TryGetValue(parameter, out var v) ? v : null;
}

/// <summary>
/// A Solidity event definition parsed from a human-readable signature such as
/// <c>Transfer(address indexed from, address indexed to, uint256 value)</c>.
/// Decodes matching logs: indexed static parameters come from the topics, indexed dynamic ones (strings, bytes, arrays,
/// tuples) are only available as their keccak-256 hash (<c>byte[32]</c>), the rest from the data.
/// </summary>
public sealed class EvmEvent
{
    private EvmEvent(string name, IReadOnlyList<EvmEventParameter> parameters, bool anonymous)
    {
        Name = name;
        Parameters = parameters;
        Anonymous = anonymous;
        Signature = $"{name}({string.Join(",", parameters.Select(p => p.Type.Name))})";
        Topic0 = HexUtil.Encode(EvmAbi.GetEventTopic(Signature));
    }

    public string Name { get; }
    public IReadOnlyList<EvmEventParameter> Parameters { get; }
    public bool Anonymous { get; }

    /// <summary>Canonical signature, e.g. <c>Transfer(address,address,uint256)</c>.</summary>
    public string Signature { get; }

    /// <summary>First topic (0x-hex keccak-256 of <see cref="Signature"/>); absent from anonymous events' logs.</summary>
    public string Topic0 { get; }

    /// <summary>ERC-20 / ERC-721 <c>Transfer</c> (for ERC-721 the third parameter is the indexed token id).</summary>
    public static EvmEvent Erc20Transfer { get; } = Parse("Transfer(address indexed from, address indexed to, uint256 value)");

    public static EvmEvent Erc20Approval { get; } = Parse("Approval(address indexed owner, address indexed spender, uint256 value)");

    /// <summary>Parses <c>[event] Name(type [indexed] [name], ...) [anonymous]</c>.</summary>
    public static EvmEvent Parse(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        string s = signature.Trim().TrimEnd(';');
        if (s.StartsWith("event ", StringComparison.Ordinal)) s = s[6..].TrimStart();
        bool anonymous = false;
        if (s.EndsWith("anonymous", StringComparison.Ordinal))
        {
            anonymous = true;
            s = s[..^9].TrimEnd();
        }
        int open = s.IndexOf('(');
        if (open <= 0 || !s.EndsWith(')')) throw new FormatException($"Invalid event signature '{signature}'");
        string name = s[..open].Trim();
        var parameters = new List<EvmEventParameter>();
        int unnamed = 0;
        foreach (string raw in AbiType.SplitTopLevel(s[(open + 1)..^1]).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            // Split off the type (which may itself contain spaces inside a tuple), then "indexed" and the name.
            int depth = 0, end = raw.Length;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '(') depth++;
                else if (raw[i] == ')') depth--;
                else if (raw[i] == ' ' && depth == 0) { end = i; break; }
            }
            var type = AbiType.Parse(raw[..end]);
            var words = raw[end..].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            bool indexed = words.Remove("indexed");
            string pname = words.Count > 0 ? words[^1] : $"arg{unnamed}";
            unnamed++;
            parameters.Add(new EvmEventParameter(pname, type, indexed));
        }
        if (parameters.Count(p => p.Indexed) > (anonymous ? 4 : 3)) throw new FormatException("Too many indexed parameters");
        return new EvmEvent(name, parameters, anonymous);
    }

    /// <summary>True when <paramref name="log"/> was emitted by this event (topic 0 and topic count match).</summary>
    public bool Matches(EvmLog log)
    {
        int expectedTopics = Parameters.Count(p => p.Indexed) + (Anonymous ? 0 : 1);
        if (log.Topics.Count != expectedTopics) return false;
        return Anonymous || string.Equals(log.Topics[0], Topic0, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryDecode(EvmLog log, out EvmDecodedEvent? decoded)
    {
        decoded = null;
        if (!Matches(log)) return false;
        try
        {
            decoded = Decode(log);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public EvmDecodedEvent Decode(EvmLog log)
    {
        if (!Matches(log)) throw new FormatException($"Log does not match event {Signature}");
        var dataTypes = Parameters.Where(p => !p.Indexed).Select(p => p.Type).ToList();
        object?[] dataValues;
        try
        {
            dataValues = EvmAbi.Decode(dataTypes, HexUtil.Decode(log.Data));
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new FormatException($"Log data does not decode as {Signature}", ex);
        }

        var values = new Dictionary<string, object?>(Parameters.Count, StringComparer.Ordinal);
        int topic = Anonymous ? 0 : 1, data = 0;
        foreach (var p in Parameters)
        {
            if (p.Indexed)
            {
                byte[] word = HexUtil.Decode(log.Topics[topic++]);
                if (word.Length != 32) throw new FormatException("Topics must be 32 bytes");
                values[p.Name] = p.Type.IsDynamic || p.Type is AbiType.Array or AbiType.Tuple ? word : EvmAbi.Decode([p.Type], word)[0];
            }
            else
            {
                values[p.Name] = dataValues[data++];
            }
        }
        return new EvmDecodedEvent(Name, values, log);
    }

    public override string ToString() => Signature;
}
