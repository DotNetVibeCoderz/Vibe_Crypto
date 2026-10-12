using System;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Crypto.Net.Generators;

/// <summary>
/// Turns <c>*.abi.json</c> (Solidity ABI or Hardhat/Foundry artifact) and <c>*.idl.json</c> (Anchor IDL) additional files
/// into typed clients. The class name comes from the file name (<c>Erc20.abi.json</c> → <c>Erc20</c>) or the
/// <c>CryptoNetName</c> item metadata; the namespace from <c>CryptoNetNamespace</c> or the project's root namespace.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class CryptoNetSourceGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidInput = new(
        "CNETGEN001", "Invalid contract description",
        "Could not generate a client from '{0}': {1}",
        "Crypto.Net.Generators", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(f => f.Path.EndsWith(".abi.json", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".idl.json", StringComparison.OrdinalIgnoreCase))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select((pair, ct) =>
            {
                var (file, options) = pair;
                var fileOptions = options.GetOptions(file);
                fileOptions.TryGetValue("build_metadata.AdditionalFiles.CryptoNetName", out string? name);
                fileOptions.TryGetValue("build_metadata.AdditionalFiles.CryptoNetNamespace", out string? ns);
                options.GlobalOptions.TryGetValue("build_property.RootNamespace", out string? rootNs);
                string fileName = Path.GetFileName(file.Path);
                string baseName = fileName.Substring(0, fileName.Length - ".abi.json".Length);
                return new Input(
                    file.Path,
                    file.GetText(ct)?.ToString() ?? "",
                    string.IsNullOrWhiteSpace(name) ? baseName : name!,
                    string.IsNullOrWhiteSpace(ns) ? rootNs ?? "" : ns!,
                    fileName.EndsWith(".idl.json", StringComparison.OrdinalIgnoreCase));
            });

        context.RegisterSourceOutput(files, (spc, input) =>
        {
            try
            {
                string source = input.IsAnchor
                    ? AnchorClientGenerator.Generate(input.Text, input.Namespace, input.Name)
                    : EvmContractGenerator.Generate(input.Text, input.Namespace, input.Name);
                spc.AddSource($"{Names.Pascal(input.Name, "Contract")}.{(input.IsAnchor ? "Anchor" : "Evm")}.g.cs", SourceText.From(source, Encoding.UTF8));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or OverflowException)
            {
                spc.ReportDiagnostic(Diagnostic.Create(InvalidInput, Location.None, input.Path, ex.Message));
            }
        });
    }

    private sealed record Input(string Path, string Text, string Name, string Namespace, bool IsAnchor);
}
