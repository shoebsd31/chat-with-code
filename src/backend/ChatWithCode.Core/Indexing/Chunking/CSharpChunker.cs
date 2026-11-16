using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>
/// C# via Roslyn. Small files stay whole. Otherwise each type that fits becomes one chunk;
/// bigger types are split into groups of members (methods, properties, …), and members too
/// big on their own are cut into windows. Chunks are named like "Contoso.Orders.OrderService.Submit".
/// Code outside types (top-level statements) is chunked by blocks; using directives are dropped.
/// </summary>
public sealed class CSharpChunker(ITokenCounter tokens, ChunkingOptions options) : IChunker
{
    public IReadOnlyList<TextChunk> Chunk(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var source = new SourceText(text, tokens);
        var chunks = new List<TextChunk>();
        int first = 0, last = source.Lines.Length - 1;
        if (!source.Trim(ref first, ref last))
        {
            return chunks;
        }

        var root = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)).GetCompilationUnitRoot();
        var topTypes = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<BaseTypeDeclarationSyntax>()
            .ToList();

        if (source.TokensIn(first, last) <= options.MaxTokens)
        {
            chunks.Add(source.ToChunk(first, last, topTypes.Count > 0 ? QualifiedName(topTypes[0]) : null));
            return chunks;
        }

        var covered = new bool[source.Lines.Length];
        foreach (var type in topTypes)
        {
            ChunkType(type, source, chunks, covered);
        }

        ChunkUncovered(source, covered, chunks);
        chunks.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return chunks;
    }

    private void ChunkType(BaseTypeDeclarationSyntax type, SourceText source, List<TextChunk> chunks, bool[] covered)
    {
        var (start, end) = LineRange(type, source);
        Mark(covered, start, end);
        var typeName = QualifiedName(type);

        if (source.TokensIn(start, end) <= options.MaxTokens || type is not TypeDeclarationSyntax withMembers)
        {
            if (source.TokensIn(start, end) <= options.MaxTokens)
            {
                chunks.Add(source.ToChunk(start, end, typeName));
            }
            else
            {
                source.SplitWindows(start, end, typeName, options, chunks); // e.g. a huge enum
            }

            return;
        }

        // Group consecutive members up to the limit. Nested types are chunked on their own.
        int? groupStart = null;
        var groupEnd = -1;
        var groupMembers = new List<string>();
        foreach (var member in withMembers.Members)
        {
            if (member is BaseTypeDeclarationSyntax nested)
            {
                Flush();
                ChunkType(nested, source, chunks, covered);
                continue;
            }

            var (ms, me) = LineRange(member, source);
            var memberName = $"{typeName}.{MemberName(member)}";
            if (source.TokensIn(ms, me) > options.MaxTokens)
            {
                Flush();
                source.SplitWindows(ms, me, memberName, options, chunks);
                continue;
            }

            if (groupStart is { } gs && source.TokensIn(gs, me) > options.MaxTokens)
            {
                Flush();
            }

            groupStart ??= ms;
            groupEnd = me;
            groupMembers.Add(memberName);
        }

        Flush();

        void Flush()
        {
            if (groupStart is { } gs)
            {
                chunks.Add(source.ToChunk(gs, groupEnd, groupMembers.Count == 1 ? groupMembers[0] : typeName));
                groupStart = null;
                groupMembers.Clear();
            }
        }
    }

    /// <summary>Lines outside any type: top-level statements, delegates, attributes. Using directives are skipped.</summary>
    private void ChunkUncovered(SourceText source, bool[] covered, List<TextChunk> chunks)
    {
        var start = -1;
        for (var i = 0; i <= covered.Length; i++)
        {
            var isFree = i < covered.Length && !covered[i];
            if (isFree && start < 0)
            {
                start = i;
            }
            else if (!isFree && start >= 0)
            {
                if (HasSubstance(source, start, i - 1))
                {
                    source.ChunkByBlocks(start, i - 1, symbol: null, options, chunks, (s, e) => GenericChunker.FindSymbol(source.Lines, s, e));
                }

                start = -1;
            }
        }
    }

    private static bool HasSubstance(SourceText source, int start, int end)
    {
        for (var i = start; i <= end; i++)
        {
            var line = source.Lines[i].Trim();
            if (line.Length == 0 || line is "{" or "}" || line.StartsWith("using ", StringComparison.Ordinal) ||
                line.StartsWith("global using ", StringComparison.Ordinal) || line.StartsWith("namespace ", StringComparison.Ordinal) ||
                line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#'))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>0-based line range of a node, including its doc comments and attributes.</summary>
    private static (int Start, int End) LineRange(SyntaxNode node, SourceText source)
    {
        var tree = node.SyntaxTree;
        var startPosition = node.SpanStart;
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                startPosition = Math.Min(startPosition, trivia.SpanStart);
            }
        }

        var start = tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(startPosition, 0)).StartLinePosition.Line;
        var end = tree.GetLineSpan(node.Span).EndLinePosition.Line;
        return (start, Math.Min(end, source.Lines.Length - 1));
    }

    private static void Mark(bool[] covered, int start, int end)
    {
        for (var i = start; i <= end; i++)
        {
            covered[i] = true;
        }
    }

    private static string QualifiedName(BaseTypeDeclarationSyntax type)
    {
        var parts = new List<string> { type.Identifier.ValueText };
        for (var node = type.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax outer:
                    parts.Add(outer.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString());
                    break;
            }
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    private static string MemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
        DestructorDeclarationSyntax d => "~" + d.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        EventDeclarationSyntax e => e.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        OperatorDeclarationSyntax o => "operator " + o.OperatorToken.ValueText,
        ConversionOperatorDeclarationSyntax c => "operator " + c.Type,
        BaseFieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        DelegateDeclarationSyntax d => d.Identifier.ValueText,
        _ => member.Kind().ToString(),
    };
}
