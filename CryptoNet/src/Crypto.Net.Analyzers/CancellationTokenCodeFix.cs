using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Crypto.Net.Analyzers;

/// <summary>CNET005 fix: passes the enclosing CancellationToken as a named argument.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CancellationTokenCodeFix)), Shared]
public sealed class CancellationTokenCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(CryptoNetDiagnostics.MissingCancellationToken.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return;
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(CryptoNetDiagnostics.TokenNameKey, out var token) || token is null) continue;
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not { } node) continue;
            var invocation = node as InvocationExpressionSyntax ?? node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (invocation is null || model.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method) continue;
            var parameter = method.Parameters.FirstOrDefault(p => p.Type.ToDisplayString() == "System.Threading.CancellationToken");
            if (parameter is null) continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Pass '{token}'",
                    ct => AddArgumentAsync(context.Document, root, invocation, parameter.Name, token),
                    equivalenceKey: "CNET005"),
                diagnostic);
        }
    }

    private static Task<Document> AddArgumentAsync(Document document, SyntaxNode root, InvocationExpressionSyntax invocation, string parameterName, string token)
    {
        var argument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(token))
            .WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(parameterName)));
        var updated = invocation.WithArgumentList(invocation.ArgumentList.AddArguments(argument));
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(invocation, updated)));
    }
}
