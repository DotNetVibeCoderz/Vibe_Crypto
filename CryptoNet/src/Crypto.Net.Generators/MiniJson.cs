using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Crypto.Net.Generators;

/// <summary>
/// Small JSON reader (objects → <see cref="Dictionary{TKey,TValue}"/>, arrays → <see cref="List{T}"/>, numbers →
/// <see cref="double"/>/<see cref="long"/>). Source generators cannot rely on System.Text.Json being loadable inside
/// every compiler host, so the generator carries its own parser.
/// </summary>
internal static class MiniJson
{
    public static object? Parse(string text)
    {
        int pos = 0;
        object? value = ReadValue(text, ref pos, 0);
        SkipWhitespace(text, ref pos);
        if (pos != text.Length) throw new FormatException($"Unexpected character at {pos}");
        return value;
    }

    private static object? ReadValue(string s, ref int pos, int depth)
    {
        if (depth > 128) throw new FormatException("JSON nesting too deep");
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length) throw new FormatException("Unexpected end of JSON");
        char c = s[pos];
        switch (c)
        {
            case '{':
            {
                pos++;
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                SkipWhitespace(s, ref pos);
                if (Peek(s, pos) == '}') { pos++; return obj; }
                while (true)
                {
                    SkipWhitespace(s, ref pos);
                    string key = ReadString(s, ref pos);
                    SkipWhitespace(s, ref pos);
                    Expect(s, ref pos, ':');
                    obj[key] = ReadValue(s, ref pos, depth + 1);
                    SkipWhitespace(s, ref pos);
                    if (Peek(s, pos) == ',') { pos++; continue; }
                    Expect(s, ref pos, '}');
                    return obj;
                }
            }
            case '[':
            {
                pos++;
                var list = new List<object?>();
                SkipWhitespace(s, ref pos);
                if (Peek(s, pos) == ']') { pos++; return list; }
                while (true)
                {
                    list.Add(ReadValue(s, ref pos, depth + 1));
                    SkipWhitespace(s, ref pos);
                    if (Peek(s, pos) == ',') { pos++; continue; }
                    Expect(s, ref pos, ']');
                    return list;
                }
            }
            case '"':
                return ReadString(s, ref pos);
            case 't':
                Literal(s, ref pos, "true");
                return true;
            case 'f':
                Literal(s, ref pos, "false");
                return false;
            case 'n':
                Literal(s, ref pos, "null");
                return null;
            default:
                return ReadNumber(s, ref pos);
        }
    }

    private static object ReadNumber(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length && "+-0123456789.eE".IndexOf(s[pos]) >= 0) pos++;
        string n = s.Substring(start, pos - start);
        if (n.Length == 0) throw new FormatException($"Unexpected character '{s[start]}' at {start}");
        if (long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
        return double.Parse(n, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string ReadString(string s, ref int pos)
    {
        Expect(s, ref pos, '"');
        var sb = new StringBuilder();
        while (true)
        {
            if (pos >= s.Length) throw new FormatException("Unterminated string");
            char c = s[pos++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (pos >= s.Length) throw new FormatException("Unterminated escape");
            char e = s[pos++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (pos + 4 > s.Length) throw new FormatException("Bad unicode escape");
                    sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    pos += 4;
                    break;
                default: throw new FormatException($"Bad escape \\{e}");
            }
        }
    }

    private static void Literal(string s, ref int pos, string word)
    {
        if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0) throw new FormatException($"Expected {word} at {pos}");
        pos += word.Length;
    }

    private static void Expect(string s, ref int pos, char c)
    {
        if (Peek(s, pos) != c) throw new FormatException($"Expected '{c}' at {pos}");
        pos++;
    }

    private static char Peek(string s, int pos) => pos < s.Length ? s[pos] : '\0';

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    // Accessors used by the generators.
    public static Dictionary<string, object?> Obj(object? v) => v as Dictionary<string, object?> ?? throw new FormatException("Expected a JSON object");
    public static List<object?> Arr(object? v) => v as List<object?> ?? new List<object?>();
    public static string? Str(Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) ? v as string : null;
    public static bool Bool(Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) && v is bool b && b;
    public static List<object?> Arr(Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) ? Arr(v) : new List<object?>();
    public static object? Get(Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Helpers for C# identifiers and source text.</summary>
internal static class Names
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate","do",
        "double","else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
        "internal","is","lock","long","namespace","new","null","object","operator","out","override","params","private","protected","public","readonly",
        "ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof","uint",
        "ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while","value",
    };

    /// <summary>A valid identifier: letters, digits and underscores; never starting with a digit.</summary>
    public static string Clean(string? name, string fallback)
    {
        if (string.IsNullOrEmpty(name)) return fallback;
        var sb = new StringBuilder();
        foreach (char c in name!) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        string s = sb.ToString().Trim('_');
        if (s.Length == 0) return fallback;
        if (char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    /// <summary>PascalCase from camelCase, snake_case or kebab-case.</summary>
    public static string Pascal(string? name, string fallback = "Value")
    {
        string clean = Clean(name, fallback);
        var sb = new StringBuilder();
        bool upper = true;
        foreach (char c in clean)
        {
            if (c == '_') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        string s = sb.Length == 0 ? fallback : sb.ToString();
        return char.IsDigit(s[0]) ? "_" + s : s;
    }

    /// <summary>camelCase parameter name, escaped when it is a keyword.</summary>
    public static string Camel(string? name, string fallback)
    {
        string p = Pascal(name, fallback);
        string s = p.Length > 1 && char.IsUpper(p[1]) && p.ToUpperInvariant() == p ? p.ToLowerInvariant() : char.ToLowerInvariant(p[0]) + p.Substring(1);
        return Keywords.Contains(s) ? "@" + s : s;
    }

    /// <summary>snake_case (Anchor's Rust instruction names).</summary>
    public static string Snake(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && name[i - 1] != '_') sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Literal(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>Indented source builder.</summary>
internal sealed class Code
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public Code Line(string text = "")
    {
        if (text.Length == 0) { _sb.Append('\n'); return this; }
        if (text.StartsWith("}", StringComparison.Ordinal)) _indent--;
        _sb.Append(' ', _indent * 4).Append(text).Append('\n');
        if (text.EndsWith("{", StringComparison.Ordinal)) _indent++;
        return this;
    }

    public override string ToString() => _sb.ToString();
}
