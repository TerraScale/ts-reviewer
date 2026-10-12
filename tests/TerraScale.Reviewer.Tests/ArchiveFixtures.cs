using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

internal static class ArchiveFixtures
{
    internal static byte[] Create(params (string Path, string Content, TarEntryType Type)[] files)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "git archive commit metadata" }));
            foreach (var (path, content, type) in files)
            {
                var entry = new PaxTarEntry(type, path);
                if (type is TarEntryType.RegularFile) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                else if (type == TarEntryType.SymbolicLink) entry.LinkName = content;
                writer.WriteEntry(entry);
                entry.DataStream?.Dispose();
            }
        }
        return output.ToArray();
    }
    internal static byte[] Widget => Create(("TerraScale-widgets-commit/src/Widget.cs",
        "public class Widget { public Widget CreateWidget() => new(); }", TarEntryType.RegularFile));
}
