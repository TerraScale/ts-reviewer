using System.Text;
using System.Text.RegularExpressions;

namespace TerraScale.Reviewer.Features.Context;

internal static partial class CodeChunker
{
    private const int ChunkLines = 80;
    private const int OverlapLines = 12;

    public static IReadOnlyList<CodeChunk> Split(string path, string source)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var chunks = new List<CodeChunk>();
        for (var start = 0; start < lines.Length; start += ChunkLines - OverlapLines)
        {
            var end = Math.Min(lines.Length, start + ChunkLines);
            var content = string.Join('\n', lines[start..end]).Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;
            var symbol = FindSymbol(lines, start, end) ?? Path.GetFileName(path);
            chunks.Add(new CodeChunk(path, start + 1, end, symbol, content));
            if (end == lines.Length) break;
        }
        return chunks;
    }

    private static string? FindSymbol(string[] lines, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var match = SymbolPattern().Match(lines[i]);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    [GeneratedRegex(@"(?:class|struct|interface|enum|record|fn|func|function|def|void|async|public|private|protected|internal)\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex SymbolPattern();
}
