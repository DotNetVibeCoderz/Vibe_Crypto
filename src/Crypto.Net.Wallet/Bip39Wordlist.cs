using System.Reflection;

namespace Crypto.Net.Wallet;

/// <summary>
/// BIP-0039 standard English wordlist (2048 words).
/// </summary>
public static class Bip39Wordlist
{
    private static readonly string[] _words;
    private static readonly Dictionary<string, int> _wordToIndex;

    static Bip39Wordlist()
    {
        var assembly = typeof(Bip39Wordlist).Assembly;
        using var stream = assembly.GetManifestResourceStream("Crypto.Net.Wallet.Resources.bip39_english.txt")
            ?? throw new InvalidOperationException("Embedded resource bip39_english.txt not found");
        using var reader = new StreamReader(stream);
        string content = reader.ReadToEnd();
        _words = content.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (_words.Length != 2048)
        {
            throw new InvalidOperationException($"Invalid BIP39 wordlist count: expected 2048, got {_words.Length}");
        }

        _wordToIndex = new Dictionary<string, int>(2048, StringComparer.Ordinal);
        for (int i = 0; i < _words.Length; i++)
        {
            _wordToIndex[_words[i]] = i;
        }
    }

    public static IReadOnlyList<string> Words => _words;

    public static string GetWord(int index)
    {
        if (index < 0 || index >= 2048) throw new ArgumentOutOfRangeException(nameof(index));
        return _words[index];
    }

    public static int GetIndex(string word)
    {
        if (_wordToIndex.TryGetValue(word, out int index)) return index;
        return -1;
    }
}
