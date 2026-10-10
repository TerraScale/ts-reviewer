namespace TerraScale.Reviewer.Features.Forgejo;

public static class UnifiedDiff
{
    public static IReadOnlyList<ChangedFile> Parse(string raw)
    {
        using var reader = new StringReader(raw);
        return Parse(reader);
    }

    public static IReadOnlyList<ChangedFile> Parse(TextReader reader, IReadOnlySet<string>? paths = null)
    {
        var files = new List<ChangedFile>();
        string? filename = null;
        var patch = new System.Text.StringBuilder();
        var keep = true;
        var inHunk = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Finish();
                filename = null;
                patch.Clear();
                keep = true;
                inHunk = false;
                continue;
            }
            if (line.StartsWith("@@ ", StringComparison.Ordinal)) inHunk = true;
            if (!inHunk && (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)))
            {
                var path = DecodePath(line[4..]);
                if (path != "/dev/null") filename = path[2..];
                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    keep = filename is not null && (paths is null || paths.Contains(filename));
                    if (!keep) patch.Clear();
                }
            }
            if (keep) patch.Append(line).Append('\n');
        }
        Finish();
        return files;

        void Finish()
        {
            if (keep && filename is not null) files.Add(new ChangedFile(filename, "", patch.ToString(), 0, 0));
        }
    }

    private static string DecodePath(string path)
    {
        path = path.TrimEnd('\t');
        if (!path.StartsWith('"')) return path;
        using var bytes = new MemoryStream();
        for (var i = 1; i < path.Length - 1;)
        {
            if (path[i] != '\\')
            {
                var start = i;
                while (i < path.Length - 1 && path[i] != '\\') i++;
                bytes.Write(System.Text.Encoding.UTF8.GetBytes(path[start..i]));
                continue;
            }
            var escaped = path[++i];
            i++;
            // Git's octal escapes encode UTF-8 bytes.
            if (escaped is >= '0' and <= '7')
            {
                var value = escaped - '0';
                for (var digits = 1; digits < 3 && i < path.Length - 1 && path[i] is >= '0' and <= '7'; digits++)
                    value = value * 8 + path[i++] - '0';
                bytes.WriteByte((byte)value);
            }
            else bytes.WriteByte(escaped switch
            {
                'a' => 7,
                'b' => 8,
                't' => 9,
                'n' => 10,
                'v' => 11,
                'f' => 12,
                'r' => 13,
                _ => (byte)escaped,
            });
        }
        return System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
    }

    public static int ReviewPosition(string patch, int target)
    {
        if (target <= 0) return 0;
        var newLine = 0;
        var inHunk = false;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var plus = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.StartsWith('+'));
                if (plus is not null && int.TryParse(plus[1..].Split(',')[0], out var start)) newLine = start;
                inHunk = true;
                continue;
            }
            if (!inHunk || line.Length == 0 || line.StartsWith('\\')) continue;
            if (line.StartsWith('-')) continue;
            if (line.StartsWith('+') || line.StartsWith(' '))
            {
                if (newLine == target) return target;
                newLine++;
            }
        }
        return 0;
    }
}
