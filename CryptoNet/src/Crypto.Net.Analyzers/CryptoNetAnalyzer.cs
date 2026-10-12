using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Crypto.Net.Analyzers;

/// <summary>Diagnostic descriptors shared by the analyzer and the code fix.</summary>
public static class CryptoNetDiagnostics
{
    private const string Category = "Crypto.Net.Security";
    private const string HelpBase = "https://github.com/DotNetVibeCoderz/Vibe_Crypto/blob/main/CryptoNet/docs/en/analyzers.md#";

    public static readonly DiagnosticDescriptor HardcodedSecret = new(
        "CNET001", "Hard-coded private key or mnemonic",
        "{0} is hard-coded in source; load it from a key store, user secrets or environment instead",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Private keys, WIF strings and BIP-39 phrases in source code end up in version control and binaries.",
        helpLinkUri: HelpBase + "cnet001");

    public static readonly DiagnosticDescriptor SecretInString = new(
        "CNET002", "Secret kept in a string",
        "'{0}' keeps a secret in a string that cannot be wiped; use SecureBuffer (or byte[] cleared after use)",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Strings are immutable and interned copies linger in memory and crash dumps.",
        helpLinkUri: HelpBase + "cnet002");

    public static readonly DiagnosticDescriptor AmountFromFloatingPoint = new(
        "CNET003", "Amount created from floating point",
        "Amount is created from a {0} value; binary floating point cannot represent token amounts exactly — use decimal, Amount.Parse or base units",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: HelpBase + "cnet003");

    public static readonly DiagnosticDescriptor MainnetInDebug = new(
        "CNET004", "Mainnet client in a Debug build",
        "'{0}' connects to a mainnet ({1}) in a Debug build; use a testnet while developing",
        Category, DiagnosticSeverity.Info, isEnabledByDefault: true,
        helpLinkUri: HelpBase + "cnet004");

    public static readonly DiagnosticDescriptor MissingCancellationToken = new(
        "CNET005", "RPC call without the available CancellationToken",
        "Pass '{0}' to '{1}' so the network call can be cancelled",
        "Crypto.Net.Reliability", DiagnosticSeverity.Info, isEnabledByDefault: true,
        helpLinkUri: HelpBase + "cnet005");

    /// <summary>Property key carrying the name of the token to pass (used by the code fix).</summary>
    public const string TokenNameKey = "TokenName";
}

/// <summary>Analyzer implementing CNET001–CNET005.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CryptoNetAnalyzer : DiagnosticAnalyzer
{
    private static readonly Lazy<HashSet<string>> Bip39Words = new(LoadWordlist);
    private static readonly Regex Hex64 = new("^(0x)?[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Wif = new("^[5KLc9][1-9A-HJ-NP-Za-km-z]{50,51}$", RegexOptions.CultureInvariant);
    private static readonly Regex Xprv = new("^[xtyzuv]prv[1-9A-HJ-NP-Za-km-z]{107,108}$", RegexOptions.CultureInvariant);
    private static readonly Regex SecretName = new("(private_?key|secret_?key|mnemonic|seed_?phrase|recovery_?phrase|passphrase|password|wif|xprv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, HashSet<string>> Mainnets = new(StringComparer.Ordinal)
    {
        ["Crypto.Net.Evm.EvmChain"] = ["Ethereum", "Polygon", "Arbitrum", "Optimism", "Base", "BnbChain", "Avalanche"],
        ["Crypto.Net.Bitcoin.BitcoinNetwork"] = ["Mainnet"],
        ["Crypto.Net.Solana.SolanaChain"] = ["MainnetBeta"],
        ["Crypto.Net.Polkadot.PolkadotChain"] = ["Polkadot", "PolkadotAssetHub", "Kusama", "KusamaAssetHub"],
        ["Crypto.Net.Cosmos.CosmosChain"] = ["CosmosHub", "Osmosis"],
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        CryptoNetDiagnostics.HardcodedSecret,
        CryptoNetDiagnostics.SecretInString,
        CryptoNetDiagnostics.AmountFromFloatingPoint,
        CryptoNetDiagnostics.MainnetInDebug,
        CryptoNetDiagnostics.MissingCancellationToken);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;
            var amount = compilation.GetTypeByMetadataName("Crypto.Net.Core.Amount");
            var chainClient = compilation.GetTypeByMetadataName("Crypto.Net.Core.IChainClient");
            var jsonRpc = compilation.GetTypeByMetadataName("Crypto.Net.Core.JsonRpcClient");
            var wsRpc = compilation.GetTypeByMetadataName("Crypto.Net.Core.WebSocketRpcClient");
            var cancellationToken = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
            var bigInteger = compilation.GetTypeByMetadataName("System.Numerics.BigInteger");
            bool debug = compilation.Options.OptimizationLevel == OptimizationLevel.Debug;

            start.RegisterOperationAction(c => AnalyzeLiteral(c), OperationKind.Literal);
            start.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Field, SymbolKind.Property);
            if (amount is not null)
                start.RegisterOperationAction(c => AnalyzeAmount(c, amount, bigInteger), OperationKind.Invocation, OperationKind.ObjectCreation);
            if (chainClient is not null && debug)
                start.RegisterOperationAction(c => AnalyzeMainnet(c, chainClient), OperationKind.ObjectCreation);
            if (cancellationToken is not null && (chainClient is not null || jsonRpc is not null || wsRpc is not null))
                start.RegisterOperationAction(c => AnalyzeCancellation(c, cancellationToken, chainClient, jsonRpc, wsRpc), OperationKind.Invocation);
        });
    }

    #region CNET001

    private static void AnalyzeLiteral(OperationAnalysisContext context)
    {
        var literal = (ILiteralOperation)context.Operation;
        if (literal.ConstantValue is not { HasValue: true, Value: string text } || text.Length < 20) return;
        string trimmed = text.Trim();
        string? kind = null;
        if (IsMnemonic(trimmed)) kind = "A BIP-39 recovery phrase";
        else if (Xprv.IsMatch(trimmed)) kind = "An extended private key";
        else if (Wif.IsMatch(trimmed) && LooksLikeKeyContext(literal)) kind = "A WIF private key";
        else if (Hex64.IsMatch(trimmed) && LooksLikeKeyContext(literal)) kind = "A 32-byte private key";
        if (kind is not null)
            context.ReportDiagnostic(Diagnostic.Create(CryptoNetDiagnostics.HardcodedSecret, literal.Syntax.GetLocation(), kind));
    }

    private static bool IsMnemonic(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24)) return false;
        var list = Bip39Words.Value;
        return list.Count > 0 && words.All(w => list.Contains(w));
    }

    // 64 hex characters are also hashes and tx ids, so only flag them where a key is plausibly meant:
    // a secret-like variable/parameter name, or an argument to a method whose parameter name says key/secret.
    private static bool LooksLikeKeyContext(IOperation literal)
    {
        for (IOperation? op = literal.Parent; op is not null; op = op.Parent)
        {
            switch (op)
            {
                case IArgumentOperation { Parameter: { } p }:
                    return SecretName.IsMatch(p.Name) || p.Name.Equals("key", StringComparison.OrdinalIgnoreCase) || p.Name.IndexOf("PrivateKey", StringComparison.OrdinalIgnoreCase) >= 0;
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation d }:
                    return SecretName.IsMatch(d.Symbol.Name);
                case IFieldInitializerOperation f:
                    return f.InitializedFields.Any(x => SecretName.IsMatch(x.Name));
                case IPropertyInitializerOperation pi:
                    return pi.InitializedProperties.Any(x => SecretName.IsMatch(x.Name));
                case ISimpleAssignmentOperation { Target: var target }:
                    return target switch
                    {
                        ILocalReferenceOperation l => SecretName.IsMatch(l.Local.Name),
                        IFieldReferenceOperation fr => SecretName.IsMatch(fr.Field.Name),
                        IPropertyReferenceOperation pr => SecretName.IsMatch(pr.Property.Name),
                        _ => false,
                    };
                case IInvocationOperation or IObjectCreationOperation or IBlockOperation:
                    return false;
            }
        }
        return false;
    }

    private static HashSet<string> LoadWordlist()
    {
        using var stream = typeof(CryptoNetAnalyzer).Assembly.GetManifestResourceStream("bip39_english.txt");
        if (stream is null) return new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(stream);
        return new HashSet<string>(reader.ReadToEnd().Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    #endregion

    #region CNET002

    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        var (type, name) = context.Symbol switch
        {
            IFieldSymbol f when !f.IsConst && !f.IsImplicitlyDeclared => (f.Type, f.Name),
            IPropertySymbol p when !p.IsIndexer => (p.Type, p.Name),
            _ => (null, ""),
        };
        if (type?.SpecialType != SpecialType.System_String) return;
        if (!SecretName.IsMatch(name) || name.EndsWith("Name", StringComparison.Ordinal) || name.EndsWith("Path", StringComparison.Ordinal)
            || name.EndsWith("Hint", StringComparison.Ordinal) || name.EndsWith("Label", StringComparison.Ordinal)
            || name.EndsWith("EnvVar", StringComparison.Ordinal) || name.EndsWith("Variable", StringComparison.Ordinal)
            || name.EndsWith("File", StringComparison.Ordinal))
            return;
        foreach (var location in context.Symbol.Locations.Where(l => l.IsInSource))
            context.ReportDiagnostic(Diagnostic.Create(CryptoNetDiagnostics.SecretInString, location, name));
    }

    #endregion

    #region CNET003

    private static void AnalyzeAmount(OperationAnalysisContext context, INamedTypeSymbol amount, INamedTypeSymbol? bigInteger)
    {
        IMethodSymbol? method = context.Operation switch
        {
            IInvocationOperation i => i.TargetMethod,
            IObjectCreationOperation o => o.Constructor,
            _ => null,
        };
        if (method is null || !SymbolEqualityComparer.Default.Equals(method.ContainingType, amount)) return;
        var arguments = context.Operation is IInvocationOperation inv ? inv.Arguments : ((IObjectCreationOperation)context.Operation).Arguments;
        foreach (var argument in arguments)
        {
            if (FindFloatingPoint(argument.Value, bigInteger, 0) is { } floatType)
            {
                context.ReportDiagnostic(Diagnostic.Create(CryptoNetDiagnostics.AmountFromFloatingPoint, argument.Syntax.GetLocation(), floatType));
                return;
            }
        }
    }

    // Looks for a double/float value converted into the argument (casts, BigInteger(double), Math.Round(...)).
    private static string? FindFloatingPoint(IOperation operation, INamedTypeSymbol? bigInteger, int depth)
    {
        if (depth > 8) return null;
        switch (operation)
        {
            case IConversionOperation c when IsFloat(c.Operand.Type) && !IsFloat(c.Type):
                return Describe(c.Operand.Type!);
            case IConversionOperation c:
                return FindFloatingPoint(c.Operand, bigInteger, depth + 1);
            case IObjectCreationOperation o when bigInteger is not null && SymbolEqualityComparer.Default.Equals(o.Type, bigInteger):
                foreach (var a in o.Arguments)
                    if (IsFloat(a.Value.Type) || FindFloatingPoint(a.Value, bigInteger, depth + 1) is not null)
                        return Describe(a.Value.Type ?? o.Type!);
                return null;
            case IParenthesizedOperation p:
                return FindFloatingPoint(p.Operand, bigInteger, depth + 1);
            default:
                return null;
        }
    }

    private static bool IsFloat(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_Double or SpecialType.System_Single;

    private static string Describe(ITypeSymbol t) => t.SpecialType == SpecialType.System_Single ? "float" : "double";

    #endregion

    #region CNET004

    private static void AnalyzeMainnet(OperationAnalysisContext context, INamedTypeSymbol chainClient)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is not INamedTypeSymbol created || !created.AllInterfaces.Contains(chainClient, SymbolEqualityComparer.Default)) return;
        foreach (var argument in creation.Arguments)
        {
            var value = argument.Value;
            while (value is IConversionOperation conv) value = conv.Operand;
            if (value is IFieldReferenceOperation { Field: { IsStatic: true } field } &&
                Mainnets.TryGetValue(field.ContainingType.ToDisplayString(), out var names) && names.Contains(field.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(CryptoNetDiagnostics.MainnetInDebug, creation.Syntax.GetLocation(), created.Name, field.ContainingType.Name + "." + field.Name));
                return;
            }
        }
    }

    #endregion

    #region CNET005

    private static void AnalyzeCancellation(OperationAnalysisContext context, INamedTypeSymbol token, INamedTypeSymbol? chainClient, INamedTypeSymbol? jsonRpc, INamedTypeSymbol? wsRpc)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!IsRpcType(method.IsExtensionMethod && invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value.Type : invocation.Instance?.Type)) return;

        var tokenArgument = invocation.Arguments.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.Parameter?.Type, token));
        if (tokenArgument is null || tokenArgument.ArgumentKind != ArgumentKind.DefaultValue) return;

        string? available = FindAvailableToken(invocation, token);
        if (available is null) return;
        var properties = ImmutableDictionary<string, string?>.Empty.Add(CryptoNetDiagnostics.TokenNameKey, available);
        context.ReportDiagnostic(Diagnostic.Create(CryptoNetDiagnostics.MissingCancellationToken, invocation.Syntax.GetLocation(), properties, available, method.Name));

        bool IsRpcType(ITypeSymbol? type)
        {
            if (type is null) return false;
            if (SymbolEqualityComparer.Default.Equals(type, jsonRpc) || SymbolEqualityComparer.Default.Equals(type, wsRpc)) return true;
            if (chainClient is null) return false;
            return SymbolEqualityComparer.Default.Equals(type, chainClient) || type.AllInterfaces.Contains(chainClient, SymbolEqualityComparer.Default);
        }
    }

    // A CancellationToken parameter of the enclosing method or lambda, the innermost one first.
    private static string? FindAvailableToken(IOperation operation, INamedTypeSymbol token)
    {
        for (IOperation? op = operation.Parent; op is not null; op = op.Parent)
        {
            if (op is IAnonymousFunctionOperation lambda)
            {
                var p = lambda.Symbol.Parameters.FirstOrDefault(x => SymbolEqualityComparer.Default.Equals(x.Type, token));
                if (p is not null) return p.Name;
            }
            if (op is ILocalFunctionOperation local)
            {
                var p = local.Symbol.Parameters.FirstOrDefault(x => SymbolEqualityComparer.Default.Equals(x.Type, token));
                if (p is not null) return p.Name;
            }
        }
        var root = operation;
        while (root.Parent is not null) root = root.Parent;
        if (root.SemanticModel?.GetEnclosingSymbol(operation.Syntax.SpanStart) is IMethodSymbol method)
        {
            for (ISymbol? s = method; s is IMethodSymbol m; s = m.ContainingSymbol)
            {
                var p = m.Parameters.FirstOrDefault(x => SymbolEqualityComparer.Default.Equals(x.Type, token));
                if (p is not null) return p.Name;
            }
        }
        return null;
    }

    #endregion
}
