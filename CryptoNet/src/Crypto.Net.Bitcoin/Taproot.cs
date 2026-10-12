using Crypto.Net.Native;

namespace Crypto.Net.Bitcoin;

/// <summary>A node of a Taproot script tree: either a leaf script or a branch of two nodes.</summary>
public abstract record TaprootNode
{
    /// <summary>TapLeaf or TapBranch hash of this node.</summary>
    public abstract byte[] Hash { get; }

    /// <summary>A script leaf (BIP-341/342). Leaf version 0xC0 is Tapscript.</summary>
    public sealed record Leaf(byte[] Script, byte LeafVersion = Taproot.TapscriptLeafVersion) : TaprootNode
    {
        public override byte[] Hash => Taproot.LeafHash(Script, LeafVersion);
    }

    public sealed record Branch(TaprootNode Left, TaprootNode Right) : TaprootNode
    {
        public override byte[] Hash => Taproot.BranchHash(Left.Hash, Right.Hash);
    }
}

/// <summary>Spending information for one leaf: its script, leaf version and control block.</summary>
public sealed record TaprootLeafSpend(byte[] Script, byte LeafVersion, byte[] LeafHash, byte[] ControlBlock);

/// <summary>Taproot helpers: tagged hashes, script trees, output keys and control blocks (BIP-341).</summary>
public static class Taproot
{
    public const byte TapscriptLeafVersion = 0xC0;

    public static byte[] LeafHash(ReadOnlySpan<byte> script, byte leafVersion = TapscriptLeafVersion)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(leafVersion);
        BitcoinTransaction.WriteVarBytes(ms, script);
        return CryptoNative.TaggedHash("TapLeaf", ms.ToArray());
    }

    /// <summary>TapBranch hash; children are sorted lexicographically as BIP-341 requires.</summary>
    public static byte[] BranchHash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.SequenceCompareTo(b) <= 0
            ? CryptoNative.TaggedHash("TapBranch", [.. a, .. b])
            : CryptoNative.TaggedHash("TapBranch", [.. b, .. a]);

    /// <summary>The 32-byte tweak t = TaggedHash("TapTweak", P || merkleRoot).</summary>
    public static byte[] Tweak(ReadOnlySpan<byte> internalXOnlyKey, ReadOnlySpan<byte> merkleRoot = default) =>
        CryptoNative.TaggedHash("TapTweak", [.. internalXOnlyKey, .. merkleRoot]);

    /// <summary>Output key and parity for an internal key and optional script tree.</summary>
    public static (byte[] OutputKey, byte Parity) OutputKey(ReadOnlySpan<byte> internalXOnlyKey, TaprootNode? tree = null) =>
        CryptoNative.TaprootTweakPublicKey(internalXOnlyKey, tree is null ? default : tree.Hash);

    /// <summary>P2TR address for an internal key and optional script tree.</summary>
    public static string Address(ReadOnlySpan<byte> internalXOnlyKey, TaprootNode? tree = null, BitcoinNetwork? network = null) =>
        BitcoinAddress.FromTaprootOutputKey(OutputKey(internalXOnlyKey, tree).OutputKey, network);

    /// <summary>Control blocks for every leaf, in depth-first left-to-right order.</summary>
    public static IReadOnlyList<TaprootLeafSpend> LeafSpends(ReadOnlySpan<byte> internalXOnlyKey, TaprootNode tree)
    {
        byte parity = OutputKey(internalXOnlyKey, tree).Parity;
        byte[] key = internalXOnlyKey.ToArray();
        var result = new List<TaprootLeafSpend>();
        Walk(tree, []);
        return result;

        void Walk(TaprootNode node, List<byte[]> path)
        {
            switch (node)
            {
                case TaprootNode.Leaf leaf:
                    var cb = new List<byte> { (byte)(leaf.LeafVersion | parity) };
                    cb.AddRange(key);
                    for (int i = path.Count - 1; i >= 0; i--) cb.AddRange(path[i]);
                    result.Add(new TaprootLeafSpend(leaf.Script, leaf.LeafVersion, leaf.Hash, [.. cb]));
                    break;
                case TaprootNode.Branch branch:
                    Walk(branch.Left, [.. path, branch.Right.Hash]);
                    Walk(branch.Right, [.. path, branch.Left.Hash]);
                    break;
            }
        }
    }
}
