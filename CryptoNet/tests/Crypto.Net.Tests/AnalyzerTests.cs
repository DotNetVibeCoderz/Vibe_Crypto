using System.Collections.Immutable;
using Crypto.Net.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Crypto.Net.Tests;

/// <summary>CNET001–CNET005 against small programs compiled with the real Crypto.Net assemblies.</summary>
public class AnalyzerTests
{
    private static readonly MetadataReference[] References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(Crypto.Net.Core.Amount).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Crypto.Net.Native.CryptoNative).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Crypto.Net.Wallet.HdWallet).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Crypto.Net.Evm.EvmRpcClient).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Crypto.Net.Bitcoin.EsploraClient).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Crypto.Net.Solana.SolanaRpcClient).Assembly.Location),
    ];

    private static CSharpCompilation Compile(string source, OptimizationLevel optimization = OptimizationLevel.Debug)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create("Sample", [tree], References.DistinctBy(r => Path.GetFileName(r.Display)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimization, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        return compilation;
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, OptimizationLevel optimization = OptimizationLevel.Debug) =>
        await Compile(source, optimization).WithAnalyzers([new CryptoNetAnalyzer()]).GetAnalyzerDiagnosticsAsync();

    private static async Task<string[]> Ids(string source, OptimizationLevel optimization = OptimizationLevel.Debug) =>
        [.. (await Analyze(source, optimization)).Select(d => d.Id).OrderBy(x => x)];

    private const string Usings = "using System; using System.Threading; using System.Threading.Tasks; using System.Numerics; using Crypto.Net.Core; using Crypto.Net.Wallet; using Crypto.Net.Evm; using Crypto.Net.Solana; using Crypto.Net.Bitcoin;\n";

    [Fact]
    public async Task CNET001_FlagsMnemonicsKeysAndWif()
    {
        string src = Usings + """
            class C
            {
                const string Phrase = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
                string privateKey = "4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";
                void M()
                {
                    using var w = HdWallet.FromMnemonic("legal winner thank year wave sausage worth useful legal winner thank yellow");
                    var wif = "KwDiBf89QgGbjEhKnhXJuH7LrciVrZi3qYjgd9M7rFU73sVHnoWn";
                    var x = "xprv9s21ZrQH143K3QTDL4LXw2F7HEK3wJUD2nW2nRk4stbPy6cq3jPPqjiChkVvvNKmPGJxWUtg6LnF5kejMRNNU3TGtRBeJgk33yuGBxrMPHi";
                }
            }
            """;
        Assert.Equal(["CNET001", "CNET001", "CNET001", "CNET001", "CNET001", "CNET002"], await Ids(src)); // the field is also a secret string
    }

    [Fact]
    public async Task CNET001_IgnoresHashesAndProse()
    {
        string src = Usings + """
            class C
            {
                string txHash = "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";
                string Note = "the quick brown fox jumps over the lazy dog again and again today ok";
                void M(string s) => Console.WriteLine("4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318");
            }
            """;
        Assert.Empty(await Ids(src));
    }

    [Fact]
    public async Task CNET002_FlagsSecretStringFieldsAndProperties()
    {
        string src = Usings + """
            class Settings
            {
                public string Mnemonic { get; set; } = "";
                private string _privateKey = "";
                public string PasswordEnvVar { get; set; } = "WALLET_PASSWORD";
                public string KeyStoreFile { get; set; } = "";
                public byte[] Seed { get; set; } = [];
            }
            """;
        var diagnostics = await Analyze(src);
        Assert.Equal(["CNET002", "CNET002"], diagnostics.Select(d => d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'Mnemonic'"));
    }

    [Fact]
    public async Task CNET003_FlagsFloatingPointAmounts()
    {
        string src = Usings + """
            class C
            {
                void M(double d, float f, decimal m)
                {
                    var a = Amount.FromEther((decimal)d);
                    var b = new Amount(new BigInteger(f * 1e9), 9);
                    var ok1 = Amount.FromEther(m);
                    var ok2 = Amount.Parse("0.1", 18);
                    var ok3 = Amount.FromEther(0.1m);
                }
            }
            """;
        Assert.Equal(["CNET003", "CNET003"], await Ids(src));
    }

    [Fact]
    public async Task CNET004_FlagsMainnetClientsOnlyInDebug()
    {
        string src = Usings + """
            class C
            {
                void M()
                {
                    var main = new EvmRpcClient(EvmChain.Ethereum);
                    var test = new EvmRpcClient(EvmChain.Sepolia);
                    var sol = new SolanaRpcClient(SolanaChain.MainnetBeta);
                }
            }
            """;
        Assert.Equal(["CNET004", "CNET004"], await Ids(src));
        Assert.Empty(await Ids(src, OptimizationLevel.Release));
    }

    [Fact]
    public async Task CNET005_FlagsDroppedTokenAndFixes()
    {
        string src = Usings + """
            class C
            {
                async Task<ulong> M(EvmRpcClient rpc, CancellationToken cancellationToken)
                {
                    ulong a = await rpc.GetBlockNumberAsync();
                    ulong b = await rpc.GetBlockNumberAsync(cancellationToken);
                    return a + b;
                }
                async Task<ulong> NoToken(EvmRpcClient rpc) => await rpc.GetBlockNumberAsync();
            }
            """;
        var diagnostics = await Analyze(src);
        var d = Assert.Single(diagnostics);
        Assert.Equal("CNET005", d.Id);

        // Apply the code fix and confirm the diagnostic disappears.
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Sample", LanguageNames.CSharp)
            .WithMetadataReferences(References.DistinctBy(r => Path.GetFileName(r.Display)))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = project.AddDocument("C.cs", SourceText.From(src));
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, d, (a, _) => actions.Add(a), CancellationToken.None);
        await new CancellationTokenCodeFix().RegisterCodeFixesAsync(context);
        var operation = Assert.Single(await Assert.Single(actions).GetOperationsAsync(CancellationToken.None));
        operation.Apply(workspace, CancellationToken.None);
        string fixedText = (await workspace.CurrentSolution.GetDocument(document.Id)!.GetTextAsync()).ToString();
        Assert.Contains("GetBlockNumberAsync(ct: cancellationToken)", fixedText);
        Assert.Empty(await Ids(fixedText));
    }
}
