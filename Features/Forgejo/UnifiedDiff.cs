namespace TerraScale.Reviewer.Features.Forgejo;

public static class UnifiedDiff
{
    public static IReadOnlyList<ChangedFile> Parse(string raw)
    {
        var files = new List<ChangedFile>();
        ChangedFileBuilder? current = null;
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("diff --git a/", StringComparison.Ordinal))
            {
                var marker = line.IndexOf(" b/", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    current = new ChangedFileBuilder(line[(marker + 3)..]);
                    files.Add(current.Build());
                }
                continue;
            }
            current?.Append(line);
            if (current is not null) files[^1] = current.Build();
        }
        return files;
    }

    public static int ReviewPosition(string patch, int target)
    {
        if (target <= 0) return 0;
        var newLine = 0;
        var position = 0;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var plus = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.StartsWith('+'));
                if (plus is not null && int.TryParse(plus[1..].Split(',')[0], out var start)) newLine = start;
                continue;
            }
            if (line.StartsWith("+++") || line.StartsWith("---") || line.Length == 0) continue;
            position++;
            if (line.StartsWith('-')) continue;
            if (line.StartsWith('+') || line.StartsWith(' '))
            {
                if (newLine == target) return position;
                newLine++;
            }
        }
        return 0;
    }

    private sealed class ChangedFileBuilder(string filename)
    {
        private readonly System.Text.StringBuilder _patch = new();
        public void Append(string line) => _patch.Append(line).Append('\n');
        public ChangedFile Build() => new(filename, "", _patch.ToString(), 0, 0);
    }
}
