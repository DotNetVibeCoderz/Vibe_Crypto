using System.Text.Json;
using Crypto.Net.Bitcoin;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

/// <summary>Official BIP-174 and BIP-370 test vectors plus multisig round trips.</summary>
public class PsbtTests
{
    private static readonly JsonElement Root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "psbt-test-vectors.json"))).RootElement;
    private static readonly JsonElement V0 = Root.GetProperty("bip174");
    private static readonly JsonElement V2 = Root.GetProperty("bip370");
    private static readonly JsonElement Roles = V0.GetProperty("roles");

    public static TheoryData<string, string> Cases(string bip, string group)
    {
        var data = new TheoryData<string, string>();
        foreach (var c in Root.GetProperty(bip).GetProperty(group).EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!, c.GetProperty("hex").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases), "bip174", "invalid")]
    [MemberData(nameof(Cases), "bip370", "invalid")]
    public void Invalid_Psbts_AreRejected(string name, string hex)
    {
        Assert.False(Psbt.TryParse(hex, out _), name);
    }

    [Theory]
    [MemberData(nameof(Cases), "bip174", "valid")]
    [MemberData(nameof(Cases), "bip174", "signerFails")]
    [MemberData(nameof(Cases), "bip370", "valid")]
    public void Valid_Psbts_RoundTrip(string name, string hex)
    {
        var psbt = Psbt.Parse(hex);
        Assert.True(hex == psbt.ToHex(), name);
        Assert.Equal(psbt.ToHex(), Psbt.Parse(psbt.ToBase64()).ToHex());
    }

    [Theory]
    [MemberData(nameof(Cases), "bip174", "signerFails")]
    public void Signer_Checks_Fail(string name, string hex)
    {
        var psbt = Psbt.Parse(hex);
        byte[] key = Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000001");
        Assert.Throws<InvalidOperationException>(() => psbt.Sign(key));
        _ = name;
    }

    [Fact]
    public void Creator_Matches_Vector()
    {
        var c = Roles.GetProperty("creator");
        var tx = new BitcoinTransaction { Version = 2 };
        foreach (var i in c.GetProperty("inputs").EnumerateArray())
            tx.AddInput(i.GetProperty("txid").GetString()!, (uint)i.GetProperty("vout").GetInt32(), 0xFFFFFFFF);
        foreach (var o in c.GetProperty("outputs").EnumerateArray())
            tx.AddOutput(Convert.FromHexString(o.GetProperty("script").GetString()!), o.GetProperty("sats").GetInt64());
        Assert.Equal(c.GetProperty("psbt").GetString(), Psbt.Create(tx).ToBase64());
    }

    [Fact]
    public void Updater_Matches_Vector()
    {
        var u = Roles.GetProperty("updater");
        using var master = HdKey.ParseExtendedPrivateKey(Roles.GetProperty("masterKey").GetString()!);
        var psbt = Psbt.Parse(Roles.GetProperty("creator").GetProperty("psbt").GetString()!);
        psbt.Update(
            u.GetProperty("redeemScripts").EnumerateArray().Select(x => Convert.FromHexString(x.GetString()!)),
            u.GetProperty("witnessScripts").EnumerateArray().Select(x => Convert.FromHexString(x.GetString()!)),
            u.GetProperty("previousTxs").EnumerateArray().Select(x => BitcoinTransaction.Parse(x.GetString()!)),
            u.GetProperty("keys").EnumerateArray().Select(k => PsbtKeyOrigin.Create(
                Convert.FromHexString(k.GetProperty("pubkey").GetString()!), master.Fingerprint, k.GetProperty("path").GetString()!)));
        Assert.Equal(u.GetProperty("psbt").GetString(), psbt.ToBase64());

        psbt.SetSighashType(SigHashType.All);
        Assert.Equal(Roles.GetProperty("sighashAll").GetString(), psbt.ToBase64());
    }

    [Theory]
    [InlineData("signer1")]
    [InlineData("signer2")]
    public void Signer_Matches_Vector(string signer)
    {
        var s = Roles.GetProperty(signer);
        var psbt = Psbt.Parse(Roles.GetProperty("sighashAll").GetString()!);
        foreach (var k in s.GetProperty("keys").EnumerateArray())
        {
            using var key = BitcoinAddress.PrivateKeyFromWif(k.GetProperty("wif").GetString()!, out _, BitcoinNetwork.Testnet);
            Assert.True(psbt.Sign(key.Span) > 0);
        }
        Assert.Equal(s.GetProperty("psbt").GetString(), psbt.ToBase64());
    }

    [Fact]
    public void Signer_WithMasterKey_SignsEveryInput()
    {
        using var master = HdKey.ParseExtendedPrivateKey(Roles.GetProperty("masterKey").GetString()!);
        var psbt = Psbt.Parse(Roles.GetProperty("sighashAll").GetString()!);
        Assert.Equal(4, psbt.Sign(master));
        Assert.True(psbt.Finalize());
        Assert.Equal(Roles.GetProperty("extracted").GetString(), psbt.ExtractTransaction().ToHex());
    }

    [Fact]
    public void Combiner_Finalizer_Extractor_Match_Vectors()
    {
        var a = Psbt.Parse(Roles.GetProperty("signer1").GetProperty("psbt").GetString()!);
        var b = Psbt.Parse(Roles.GetProperty("signer2").GetProperty("psbt").GetString()!);
        string combined = Roles.GetProperty("combined").GetString()!;
        Assert.Equal(combined, Psbt.Combine(a, b).ToBase64());
        Assert.Equal(combined, Psbt.Combine(b, a).ToBase64());

        var psbt = Psbt.Parse(combined);
        Assert.True(psbt.Finalize());
        Assert.Equal(Roles.GetProperty("finalized").GetString(), psbt.ToBase64());
        Assert.Equal(Roles.GetProperty("extracted").GetString(), psbt.ExtractTransaction().ToHex());
    }

    [Theory]
    [InlineData("combineUnknown")]
    [InlineData("combineTwo")]
    public void Combiner_Vectors(string name)
    {
        var v = V0.GetProperty(name);
        var inputs = v.GetProperty("inputs").EnumerateArray().Select(x => Psbt.Parse(x.GetString()!)).ToArray();
        Assert.Equal(v.GetProperty("expected").GetString(), Psbt.Combine(inputs).ToBase64());
    }

    [Fact]
    public void V2_Conversion_RoundTrips()
    {
        foreach (var c in V2.GetProperty("valid").EnumerateArray().SkipLast(1))
        {
            var psbt = Psbt.Parse(c.GetProperty("hex").GetString()!);
            var tx = psbt.GetUnsignedTransaction();
            var v0 = psbt.ConvertTo(0);
            Assert.Equal(0u, v0.PsbtVersion);
            Assert.Equal(tx.TxId, v0.GetUnsignedTransaction().TxId);
            Assert.Equal(tx.TxId, Psbt.Parse(v0.ToBase64()).ConvertTo(2).GetUnsignedTransaction().TxId);
        }
    }

    [Fact]
    public void V2_LockTime_Determination()
    {
        var cases = V2.GetProperty("valid").EnumerateArray().Skip(13).Select(c => Psbt.Parse(c.GetProperty("hex").GetString()!)).ToList();
        uint[] expected = [0, 0, 10000, 10000, 10000, 10000, 1657048460, 1657048460];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], cases[i].GetUnsignedTransaction().LockTime);
        Assert.Throws<InvalidOperationException>(() => cases[^1].GetUnsignedTransaction());
    }

    [Theory]
    [InlineData("p2wsh")]
    [InlineData("p2sh")]
    [InlineData("p2sh-p2wsh")]
    public void Multisig_2of3_SignCombineFinalize(string kind)
    {
        using var wallet = HdWallet.FromMnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        var keys = Enumerable.Range(0, 3).Select(i => wallet.DerivePath($"m/48'/1'/0'/2'/0/{i}")).ToList();
        byte[] script = BitcoinScript.Multisig(2, keys.Select(k => k.PublicKey.ToArray()));
        Assert.True(BitcoinScript.TryParseMultisig(script, out int m, out var parsed));
        Assert.Equal(2, m);
        Assert.Equal(3, parsed.Count);

        byte[] spk = kind switch
        {
            "p2wsh" => BitcoinScript.P2WSH(CryptoNative.Sha256(script)),
            "p2sh" => BitcoinScript.P2SH(CryptoNative.Hash160(script)),
            _ => BitcoinScript.P2SH(CryptoNative.Hash160(BitcoinScript.P2WSH(CryptoNative.Sha256(script)))),
        };
        string address = kind switch
        {
            "p2wsh" => BitcoinAddress.FromScript(script, BitcoinAddressType.SegWitP2WSH, BitcoinNetwork.Testnet).Value,
            "p2sh" => BitcoinAddress.FromScript(script, BitcoinAddressType.P2SH, BitcoinNetwork.Testnet).Value,
            _ => BitcoinAddress.FromScriptNestedSegwit(script, BitcoinNetwork.Testnet).Value,
        };
        Assert.Equal(spk, BitcoinAddress.GetScriptPubKey(address, BitcoinNetwork.Testnet));

        var funding = new BitcoinTransaction();
        funding.AddInput(new string('1', 64), 0);
        funding.AddOutput(spk, 100_000);

        var spend = new BitcoinTransaction();
        spend.AddInput(funding.TxId, 0);
        spend.AddOutput(BitcoinScript.P2WPKH(new byte[20]), 99_000);
        var psbt = Psbt.Create(spend).Update(
            redeemScripts: kind == "p2sh" ? [script] : kind == "p2sh-p2wsh" ? [BitcoinScript.P2WSH(CryptoNative.Sha256(script))] : [],
            witnessScripts: kind == "p2sh" ? [] : [script],
            previousTransactions: [funding],
            keyOrigins: keys.Select((k, i) => PsbtKeyOrigin.Create(k.PublicKey.ToArray(), wallet.MasterKey.Fingerprint, $"m/48'/1'/0'/2'/0/{i}")));
        Assert.Equal(3, psbt.Inputs[0].Bip32Derivations.Count);
        Assert.Equal(1000, psbt.Fee);

        var alice = psbt.Clone();
        var carol = Psbt.Parse(psbt.ToBase64());
        Assert.Equal(1, alice.Sign(keys[0].PrivateKey.Span));
        Assert.Equal(1, carol.Sign(keys[2].PrivateKey.Span));
        Assert.False(alice.Clone().Finalize());

        var combined = Psbt.Combine(alice, carol);
        Assert.Equal(2, combined.Inputs[0].PartialSignatures.Count);
        Assert.True(combined.Finalize());
        var tx = combined.ExtractTransaction();
        Assert.Equal(kind != "p2sh", tx.HasWitness);

        // Verify both ECDSA signatures against the same sighash a validating node would compute.
        byte[] sighash = kind == "p2sh"
            ? tx.GetLegacySigHash(0, script)
            : tx.GetSegwitV0SigHash(0, script, 100_000);
        var sigs = kind == "p2sh" ? ScriptPushes(tx.Inputs[0].ScriptSig).Skip(1).SkipLast(1).ToList() : tx.Inputs[0].Witness.Skip(1).SkipLast(1).ToList();
        Assert.Equal(2, sigs.Count);
        Assert.True(VerifyDer(keys[0].PublicKey.ToArray(), sighash, sigs[0]));
        Assert.True(VerifyDer(keys[2].PublicKey.ToArray(), sighash, sigs[1]));
        keys.ForEach(k => k.Dispose());
    }

    [Fact]
    public void Taproot_KeyPath_PsbtV2()
    {
        using var wallet = HdWallet.FromMnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        using var key = wallet.DerivePath("m/86'/1'/0'/0/0");
        byte[] xonly = key.PublicKey[1..].ToArray();
        byte[] spk = BitcoinScript.P2TR(BitcoinAddress.TaprootOutputKey(xonly));

        var psbt = Psbt.CreateV2();
        var input = psbt.AddInput(new string('a', 64), 1, sequence: 0xFFFFFFFD);
        input.WitnessUtxo = new BitcoinTxOutput { ValueSats = 50_000, ScriptPubKey = spk };
        input.TapInternalKey = xonly;
        input.AddTapBip32Derivation(PsbtKeyOrigin.Create(xonly, wallet.MasterKey.Fingerprint, "m/86'/1'/0'/0/0"));
        psbt.AddOutput(BitcoinScript.P2WPKH(new byte[20]), 49_000);

        var parsed = Psbt.Parse(psbt.ToBase64());
        Assert.Equal(2u, parsed.PsbtVersion);
        Assert.Equal(1, parsed.Sign(wallet.MasterKey));
        Assert.Equal(64, parsed.Inputs[0].TapKeySignature!.Length);
        Assert.True(parsed.Finalize());
        var tx = parsed.ExtractTransaction();
        Assert.Single(tx.Inputs[0].Witness);
        var utxos = new[] { new BitcoinUtxo(tx.Inputs[0].TxId, 1, 50_000, spk) };
        Assert.True(CryptoNative.SchnorrVerify(spk[2..], tx.GetTaprootSigHash(0, utxos), tx.Inputs[0].Witness[0]));
    }

    [Fact]
    public void LegacySighash_BitcoinCoreVectors()
    {
        var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "bitcoin-core-sighash.json"))).RootElement;
        int count = 0;
        foreach (var v in vectors.EnumerateArray().Where(v => v.GetArrayLength() == 5))
        {
            var tx = BitcoinTransaction.Parse(v[0].GetString()!);
            byte[] hash = tx.GetLegacySigHash(v[2].GetInt32(), Convert.FromHexString(v[1].GetString()!), unchecked((uint)v[3].GetInt32()));
            Array.Reverse(hash);
            Assert.Equal(v[4].GetString(), Convert.ToHexStringLower(hash));
            count++;
        }
        Assert.True(count >= 500);
    }

    private static bool VerifyDer(byte[] pub, byte[] hash, byte[] derWithType)
    {
        ReadOnlySpan<byte> der = derWithType.AsSpan(0, derWithType.Length - 1);
        int rLen = der[3];
        var r = der.Slice(4, rLen).TrimStart((byte)0);
        int sLen = der[5 + rLen];
        var s = der.Slice(6 + rLen, sLen).TrimStart((byte)0);
        byte[] rs = new byte[64];
        r.CopyTo(rs.AsSpan(32 - r.Length));
        s.CopyTo(rs.AsSpan(64 - s.Length));
        return CryptoNative.Secp256k1Verify(pub, hash, rs);
    }

    private static IEnumerable<byte[]> ScriptPushes(byte[] script)
    {
        int pos = 0;
        var items = new List<byte[]>();
        while (pos < script.Length)
        {
            int op = script[pos++];
            int len = op switch
            {
                0 => 0,
                < 0x4c => op,
                0x4c => script[pos++],
                0x4d => script[pos++] | script[pos++] << 8,
                _ => throw new FormatException(),
            };
            items.Add(script.AsSpan(pos, len).ToArray());
            pos += len;
        }
        return items;
    }
}
