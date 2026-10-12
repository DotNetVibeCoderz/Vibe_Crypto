using System.Text.Json;
using Crypto.Net.Bitcoin;
using Crypto.Net.Native;

namespace Crypto.Net.Tests;

/// <summary>Official BIP-341 wallet test vectors (bitcoin/bips: bip-0341/wallet-test-vectors.json).</summary>
public class Bip341VectorTests
{
    private static readonly JsonElement Root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "bip341-wallet-test-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement e) => Convert.FromHexString(e.GetString()!);
    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    private static TaprootNode? Tree(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Array => new TaprootNode.Branch(Tree(e[0])!, Tree(e[1])!),
        _ => new TaprootNode.Leaf(Hex(e.GetProperty("script")), (byte)e.GetProperty("leafVersion").GetInt32()),
    };

    [Fact]
    public void ScriptPubKey_Vectors()
    {
        foreach (var v in Root.GetProperty("scriptPubKey").EnumerateArray())
        {
            var given = v.GetProperty("given");
            var inter = v.GetProperty("intermediary");
            var expected = v.GetProperty("expected");
            byte[] internalKey = Hex(given.GetProperty("internalPubkey"));
            TaprootNode? tree = Tree(given.GetProperty("scriptTree"));

            if (tree is not null)
            {
                Assert.Equal(inter.GetProperty("merkleRoot").GetString(), Hex(tree.Hash));
                var leafHashes = inter.GetProperty("leafHashes").EnumerateArray().Select(x => x.GetString()!).ToList();
                var spends = Taproot.LeafSpends(internalKey, tree);
                Assert.Equal(leafHashes, spends.Select(s => Hex(s.LeafHash)).ToList());
                var controlBlocks = expected.GetProperty("scriptPathControlBlocks").EnumerateArray().Select(x => x.GetString()!).ToList();
                Assert.Equal(controlBlocks, spends.Select(s => Hex(s.ControlBlock)).ToList());
            }

            Assert.Equal(inter.GetProperty("tweak").GetString(), Hex(Taproot.Tweak(internalKey, tree?.Hash ?? default)));
            var (outputKey, _) = Taproot.OutputKey(internalKey, tree);
            Assert.Equal(inter.GetProperty("tweakedPubkey").GetString(), Hex(outputKey));
            Assert.Equal(expected.GetProperty("scriptPubKey").GetString(), Hex(BitcoinScript.P2TR(outputKey)));
            Assert.Equal(expected.GetProperty("bip350Address").GetString(), Taproot.Address(internalKey, tree));
        }
    }

    [Fact]
    public void KeyPathSpending_Vectors()
    {
        var vector = Root.GetProperty("keyPathSpending")[0];
        var tx = BitcoinTransaction.Parse(vector.GetProperty("given").GetProperty("rawUnsignedTx").GetString()!);
        Assert.Equal(vector.GetProperty("given").GetProperty("rawUnsignedTx").GetString(), tx.ToHex());

        var utxos = vector.GetProperty("given").GetProperty("utxosSpent").EnumerateArray()
            .Select((u, i) => new BitcoinUtxo(tx.Inputs[i].TxId, tx.Inputs[i].Vout, u.GetProperty("amountSats").GetInt64(), Hex(u.GetProperty("scriptPubKey"))))
            .ToList();

        foreach (var spend in vector.GetProperty("inputSpending").EnumerateArray())
        {
            var given = spend.GetProperty("given");
            var inter = spend.GetProperty("intermediary");
            int index = given.GetProperty("txinIndex").GetInt32();
            var type = (SigHashType)given.GetProperty("hashType").GetInt32();
            byte[] privateKey = Hex(given.GetProperty("internalPrivkey"));
            byte[] merkleRoot = given.GetProperty("merkleRoot").ValueKind == JsonValueKind.Null ? [] : Hex(given.GetProperty("merkleRoot"));

            byte[] internalKey = CryptoNative.Secp256k1XOnlyPublicKey(privateKey);
            Assert.Equal(inter.GetProperty("internalPubkey").GetString(), Hex(internalKey));
            Assert.Equal(inter.GetProperty("tweak").GetString(), Hex(Taproot.Tweak(internalKey, merkleRoot)));
            Assert.Equal(inter.GetProperty("tweakedPrivkey").GetString(), Hex(CryptoNative.TaprootTweakSecretKey(privateKey, merkleRoot)));
            Assert.Equal(inter.GetProperty("sigMsg").GetString(), Hex(tx.GetTaprootSigMessage(index, utxos, type)));

            byte[] sigHash = tx.GetTaprootSigHash(index, utxos, type);
            Assert.Equal(inter.GetProperty("sigHash").GetString(), Hex(sigHash));

            byte[] witness = BitcoinTransaction.SignTaprootKeyPath(privateKey, sigHash, type, merkleRoot, new byte[32]);
            Assert.Equal(spend.GetProperty("expected").GetProperty("witness")[0].GetString(), Hex(witness));
        }
    }

    [Fact]
    public void FullySignedTx_RoundTrips()
    {
        var aux = Root.GetProperty("keyPathSpending")[0].GetProperty("auxiliary");
        string signed = aux.GetProperty("fullySignedTx").GetString()!;
        var tx = BitcoinTransaction.Parse(signed);
        Assert.True(tx.HasWitness);
        Assert.Equal(signed, tx.ToHex());
    }
}
