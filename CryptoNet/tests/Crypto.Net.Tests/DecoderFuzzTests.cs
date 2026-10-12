using System.IO.Compression;
using Crypto.Net.Bitcoin;
using Crypto.Net.Evm;
using Crypto.Net.Native;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;

namespace Crypto.Net.Tests;

/// <summary>
/// Mutation fuzzing of every binary decoder that reads untrusted input. Seeds are valid encodings; mutants are bit
/// flips, byte rewrites, truncations, insertions, splices and length-prefix tweaks. A decoder may accept a mutant or
/// reject it with <see cref="FormatException"/>/<see cref="ArgumentException"/>/<see cref="NotSupportedException"/>;
/// anything else (IndexOutOfRange, NullReference, Overflow, OutOfMemory, hangs) is a bug.
/// Iterations scale with <c>CRYPTONET_FUZZ_ITERATIONS</c> (default 3000 per decoder); the seed is fixed for repeatability
/// and can be changed with <c>CRYPTONET_FUZZ_SEED</c>.
/// </summary>
public class DecoderFuzzTests
{
    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("CRYPTONET_FUZZ_ITERATIONS"), out int n) ? n : 3000;
    private static readonly int Seed = int.TryParse(Environment.GetEnvironmentVariable("CRYPTONET_FUZZ_SEED"), out int s) ? s : 20261012;

    private static void Fuzz(string name, IReadOnlyList<byte[]> seeds, Action<byte[]> decode, int? iterations = null)
    {
        foreach (var seed in seeds) decode(seed); // seeds must decode
        var rng = new Random(Seed ^ name.GetHashCode(StringComparison.Ordinal));
        int count = iterations ?? Iterations;
        for (int i = 0; i < count; i++)
        {
            byte[] input = Mutate(rng, seeds[rng.Next(seeds.Count)], seeds);
            try
            {
                decode(input);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"{name}: {ex.GetType().Name} on input {Convert.ToHexStringLower(input)}\n{ex}");
            }
        }
    }

    private static byte[] Mutate(Random rng, byte[] seed, IReadOnlyList<byte[]> seeds)
    {
        var data = new List<byte>(seed);
        int rounds = 1 + rng.Next(4);
        for (int r = 0; r < rounds; r++)
        {
            switch (rng.Next(8))
            {
                case 0 when data.Count > 0:
                    int bit = rng.Next(data.Count * 8);
                    data[bit / 8] ^= (byte)(1 << (bit % 8));
                    break;
                case 1 when data.Count > 0:
                    data[rng.Next(data.Count)] = (byte)rng.Next(256);
                    break;
                case 2 when data.Count > 0:
                    int cut = rng.Next(data.Count + 1);
                    data.RemoveRange(cut, data.Count - cut);
                    break;
                case 3:
                    var extra = new byte[1 + rng.Next(16)];
                    rng.NextBytes(extra);
                    data.InsertRange(rng.Next(data.Count + 1), extra);
                    break;
                case 4 when data.Count > 0:
                    // Interesting values in place: length prefixes and compact sizes are where decoders go wrong.
                    byte[] specials = [0x00, 0x01, 0x7f, 0x80, 0xfc, 0xfd, 0xfe, 0xff, 0xb7, 0xb8, 0xbf, 0xc0, 0xf7, 0xf8, 0x03, 0x4c];
                    data[rng.Next(data.Count)] = specials[rng.Next(specials.Length)];
                    break;
                case 5:
                    var other = seeds[rng.Next(seeds.Count)];
                    if (other.Length > 0)
                    {
                        int start = rng.Next(other.Length);
                        int len = rng.Next(other.Length - start) + 1;
                        data.InsertRange(rng.Next(data.Count + 1), other.Skip(start).Take(len));
                    }
                    break;
                case 6 when data.Count > 1:
                    int from = rng.Next(data.Count);
                    data.RemoveRange(from, Math.Min(data.Count - from, 1 + rng.Next(8)));
                    break;
                default:
                    if (data.Count > 0)
                    {
                        int at = rng.Next(data.Count);
                        int dup = Math.Min(data.Count - at, 1 + rng.Next(32));
                        data.InsertRange(at, data.Skip(at).Take(dup).ToList());
                    }
                    break;
            }
        }
        return [.. data];
    }

    private static byte[] Gz(string name)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", name + ".bin.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    private static IEnumerable<string> JsonStrings(string file, params string[] path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", file)));
        var e = doc.RootElement;
        foreach (var p in path) e = e.GetProperty(p);
        return e.EnumerateArray().Select(x => x.GetProperty("hex").GetString()!).ToList();
    }

    [Fact]
    public void Psbt_Parse()
    {
        var seeds = JsonStrings("psbt-test-vectors.json", "bip174", "valid")
            .Concat(JsonStrings("psbt-test-vectors.json", "bip370", "valid"))
            .Select(Convert.FromHexString).ToList();
        Fuzz("psbt", seeds, b =>
        {
            var psbt = Psbt.Parse(b);
            _ = psbt.ToHex();
            _ = psbt.Fee;
            try
            {
                _ = psbt.GetUnsignedTransaction();
                psbt.Clone().Finalize();
            }
            catch (InvalidOperationException)
            {
                // Semantic failures (missing UTXOs, incompatible locktimes) are reported this way by design.
            }
        });
    }

    [Fact]
    public void BitcoinTransaction_Parse()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "bitcoin-core-sighash.json")));
        var seeds = doc.RootElement.EnumerateArray().Where(v => v.GetArrayLength() == 5).Take(40).Select(v => Convert.FromHexString(v[0].GetString()!)).ToList();
        Fuzz("btc-tx", seeds, b =>
        {
            var tx = BitcoinTransaction.Parse(b);
            _ = tx.Serialize();
            _ = tx.TxId;
        });
    }

    [Fact]
    public void Rlp_And_EvmTransaction_Decode()
    {
        byte[] key = Convert.FromHexString("4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318");
        var legacy = new EvmTransaction { Type = EvmTransactionType.Legacy, ChainId = 1, Nonce = 9, GasPrice = 20_000_000_000, GasLimit = 21000, To = "0x3535353535353535353535353535353535353535", Value = 1 };
        var eip1559 = new EvmTransaction { Type = EvmTransactionType.DynamicFee, ChainId = 11155111, Nonce = 1, MaxFeePerGas = 30_000_000_000, MaxPriorityFeePerGas = 1_000_000_000, GasLimit = 60000, To = "0x3535353535353535353535353535353535353535", Data = [0xa9, 0x05, 0x9c, 0xbb, 1, 2, 3] };
        var seeds = new List<byte[]>
        {
            legacy.Sign(key).RawBytes,
            eip1559.Sign(key).RawBytes,
            EvmRlp.EncodeList(EvmRlp.EncodeBytes(new byte[60]), EvmRlp.EncodeList(EvmRlp.EncodeUInt(1), EvmRlp.EncodeBytes([]))),
        };
        Fuzz("rlp", [seeds[0], seeds[2]], b => EvmRlp.Decode(b));
        Fuzz("evm-tx", seeds.Take(2).ToList(), b => EvmTransaction.DecodeSigned(b));
    }

    [Fact]
    public void Abi_Decode()
    {
        byte[] seed = EvmAbi.Encode("uint256,address,bytes,string[],bool", [new System.Numerics.BigInteger(7), "0x3535353535353535353535353535353535353535", new byte[] { 1, 2, 3 }, new[] { "a", "bc" }, true]);
        Fuzz("abi", [seed], b => EvmAbi.Decode("uint256,address,bytes,string[],bool", b));
    }

    [Fact]
    public void SolanaMessage_Parse()
    {
        string payer = CryptoNative.Base58Encode(CryptoNative.Ed25519GetPublicKey(Enumerable.Repeat((byte)1, 32).ToArray()));
        var table = new AddressLookupTableAccount(SolanaAddress.SystemProgram, [payer]);
        var legacy = new SolanaTransaction(payer, payer).Add(SystemProgram.Transfer(payer, SolanaAddress.MemoProgram, 5)).SerializeMessage();
        var v0 = new SolanaTransaction(payer, payer).Add(MemoProgram.Memo("x")).Add(SystemProgram.Transfer(payer, SolanaAddress.TokenProgram, 1)).WithLookupTables(table).SerializeMessage();
        Fuzz("solana-msg", [legacy, v0], b => SolanaMessage.Parse(b));
        Fuzz("solana-tx", [[1, .. new byte[64], .. legacy]], b => SolanaMessage.ParseTransaction(b));
        Fuzz("solana-alt", [[1, 0, 0, 0, .. Enumerable.Repeat((byte)0xff, 8), .. new byte[44], .. new byte[64]]], b => AddressLookupTableAccount.Parse("x", b));
    }

    [Fact]
    public void Substrate_Events_And_Metadata()
    {
        var md = RuntimeMetadata.Parse(Gz("asset-hub-metadata"));
        Fuzz("substrate-events", [Gz("asset-hub-events-failed"), Gz("asset-hub-events-ok")], b => SubstrateEvents.Decode(md, b));
        // Metadata is large; fewer iterations keep the suite fast while still covering the registry decoder.
        Fuzz("substrate-metadata", [Gz("asset-hub-metadata")], b => RuntimeMetadata.Parse(b), Math.Max(50, Iterations / 30));
    }

    [Fact]
    public void Sui_TransactionData_And_MoveTypeTags()
    {
        var gas = new Crypto.Net.Sui.SuiObjectRef("0x" + new string('1', 64), 5, CryptoNative.Base58Encode(new byte[32]));
        var tx = new Crypto.Net.Sui.SuiTransactionBuilder("0x" + new string('a', 64));
        var v = tx.MakeMoveVec(Crypto.Net.Core.MoveTypeTag.U64, tx.PureU64(1));
        tx.MoveCall("0x2::m::f", [Crypto.Net.Core.MoveTypeTag.Parse("0x2::coin::Coin<vector<0x2::sui::SUI>>")], v, tx.SharedObject("0x6", 1, false), tx.Receiving(gas));
        tx.TransferSui("0x5", 9);
        byte[] data = tx.Build([gas], 1000, 5_000_000, expirationEpoch: 3).Serialize();
        Fuzz("sui-tx", [data], b => Crypto.Net.Sui.SuiTransactionData.Parse(b));
        Fuzz("sui-signed", [Crypto.Net.Sui.SuiSignedTransaction.Encode(data, [new byte[97]])], b => Crypto.Net.Sui.SuiSignedTransaction.Decode(b));
        Fuzz("move-type-tag", [Crypto.Net.Core.MoveTypeTag.Parse("vector<0x1::option::Option<0x2::coin::Coin<u256>>>").ToBcs()], b =>
        {
            var r = new Crypto.Net.Core.BcsReader(b);
            Crypto.Net.Core.MoveTypeTag.Read(ref r);
        });
    }

    [Fact]
    public void Ton_BagOfCells()
    {
        using var account = new Crypto.Net.Ton.TonAccount(Enumerable.Repeat((byte)7, 32).ToArray());
        var dest = Crypto.Net.Ton.TonAddress.Parse("0:" + new string('a', 64));
        var external = account.CreateTransfer(0, [Crypto.Net.Ton.TonMessage.WithComment(dest, 5, new string('x', 150))]);
        byte[][] seeds = [external.ToBoc(), external.ToBoc(withIndex: true, withCrc: false), Crypto.Net.Ton.TonWalletContract.Code(Crypto.Net.Ton.TonWalletVersion.V4R2).ToBoc(withCrc: false)];
        Fuzz("ton-boc", seeds, b =>
        {
            foreach (var root in Crypto.Net.Ton.TonBoc.Deserialize(b))
            {
                if (!root.IsExotic) root.Hash();
            }
        });
        Fuzz("ton-ext-msg", [external.ToBoc(withCrc: false)], b => Crypto.Net.Ton.TonMessage.NormalizedHash(Crypto.Net.Ton.TonCell.FromBoc(b)));
    }

    [Fact]
    public void Cardano_TransactionsMetadataAndAddresses()
    {
        using var account = Crypto.Net.Cardano.CardanoAccount.FromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about", network: Crypto.Net.Cardano.CardanoChain.Preprod);
        var tx = new Crypto.Net.Cardano.CardanoTransaction
        {
            Inputs = [new(new string('1', 64), 1)],
            Outputs = [new(account.Address.Value, 2_000_000, [new(new string('2', 56), "41", 5)])],
            Fee = 170_000,
            Ttl = 5,
            AuxiliaryData = Crypto.Net.Cardano.CardanoMetadata.Message("fuzz me " + new string('z', 90)),
        };
        Fuzz("cardano-tx", [tx.Sign(account)], b => Crypto.Net.Cardano.CardanoTransaction.IdOf(b));
        Fuzz("cardano-cip20", [tx.AuxiliaryData], b => Crypto.Net.Cardano.CardanoMetadata.ReadMessage(b));
        Fuzz("cardano-byron", [CryptoNative.Base58Decode("Ae2tdPwUPEZFRbyhz3cpfC2CumGzNkFBN2L42rcUc2yjQpEkxDbkPodpMAi")],
            b => Crypto.Net.Cardano.CardanoAddress.IsValid(CryptoNative.Base58Encode(b)));
    }

    [Fact]
    public void Text_Codecs()
    {
        var rng = new Random(Seed);
        string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz0lIO qpzry9x8gf2tvdw0s3jn54khce6mua7l1";
        for (int i = 0; i < Iterations; i++)
        {
            string s = new([.. Enumerable.Range(0, rng.Next(0, 90)).Select(_ => alphabet[rng.Next(alphabet.Length)])]);
            foreach (Action a in new Action[]
            {
                () => CryptoNative.Base58Decode(s),
                () => CryptoNative.Base58CheckDecode(s),
                () => CryptoNative.Bech32Decode(s),
                () => CryptoNative.SegwitDecode(s),
                () => BitcoinAddress.Parse("bc1" + s),
                () => Ss58Address.Decode(s),
                () => SolanaAddress.Decode(s),
                () => AbiType.Parse(s),
            })
            {
                try
                {
                    a();
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
                {
                }
                catch (Exception ex)
                {
                    Assert.Fail($"text codec: {ex.GetType().Name} on '{s}'\n{ex}");
                }
            }
        }
    }
}
