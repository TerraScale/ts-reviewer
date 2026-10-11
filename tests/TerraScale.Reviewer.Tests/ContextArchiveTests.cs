using System.Formats.Tar;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.GitHub;
using TUnit.Assertions;
using TUnit.Core;

public sealed class ContextArchiveTests
{
    [Test]
    public async Task ThousandsOfFilesUseOneExactCommitArchiveAndIgnoreSymlinks()
    {
        var files = Enumerable.Range(0, 5_001).Select(i => ($"root/src/File{i}.cs", " ", TarEntryType.RegularFile)).ToList();
        files.Add(("root/src/Widget.cs", "class Widget {}", TarEntryType.RegularFile));
        files.Add(("root/src/Symlink.cs", "/secrets/github-app.pem", TarEntryType.SymbolicLink));
        var database = Path.GetTempFileName();
        File.Delete(database);
        var options = Options(database);
        var transport = new ArchiveTransport(ArchiveFixtures.Create([.. files]));
        using var store = new ContextStore(options);
        using var indexer = new ContextIndexer(Client(transport, options), store, new ContextEmbedder(), options, NullLogger<ContextIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);
        try
        {
            await indexer.EnsureIndexedAsync("TerraScale", "widgets", new string('a', 40), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(transport.Paths).IsEquivalentTo(new[] { "/repos/TerraScale/widgets/tarball/" + new string('a', 40) });
            await Assert.That(store.IsIndexed("TerraScale/widgets", new string('a', 40))).IsTrue();
            var matches = store.Search("TerraScale/widgets", new string('a', 40), new ContextEmbedder().Embed("github-app.pem"), 50);
            await Assert.That(matches.Any(match => match.Path == "src/Symlink.cs")).IsFalse();
        }
        finally { await indexer.StopAsync(CancellationToken.None); File.Delete(database); }
    }

    [Test]
    [Arguments("traversal")]
    [Arguments("duplicate")]
    [Arguments("multiple-roots")]
    [Arguments("file-limit")]
    [Arguments("invalid-gzip")]
    public async Task UnsafeOrIncompleteArchiveNeverMarksCommitIndexed(string failure)
    {
        var database = Path.GetTempFileName();
        File.Delete(database);
        var options = Options(database) with { ContextMaxAttempts = 1, ContextMaxFiles = 1 };
        var files = new List<(string, string, TarEntryType)> { ("root/src/A.cs", "class A {}", TarEntryType.RegularFile) };
        files.Add((failure switch { "traversal" => "root/../secrets/B.cs", "duplicate" => "root/src/A.cs", "multiple-roots" => "another/src/B.cs", _ => "root/src/B.cs" }, "class B {}", TarEntryType.RegularFile));
        var transport = new ArchiveTransport(failure == "invalid-gzip" ? [1, 2, 3, 4] : ArchiveFixtures.Create([.. files]));
        using var store = new ContextStore(options);
        using var indexer = new ContextIndexer(Client(transport, options), store, new ContextEmbedder(), options, NullLogger<ContextIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);
        try
        {
            var failed = false;
            try { await indexer.EnsureIndexedAsync("TerraScale", "widgets", new string('a', 40), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException) { failed = true; }
            await Assert.That(failed).IsTrue();
            await Assert.That(store.IsIndexed("TerraScale/widgets", new string('a', 40))).IsFalse();
            await Assert.That(store.DeadLetterJobs().Count).IsEqualTo(1);
        }
        finally { await indexer.StopAsync(CancellationToken.None); File.Delete(database); }
    }
    private static ReviewerOptions Options(string database) => new("http://localhost:0", "https://github.test", "1",
        "https://api.anthropic.com", "fixture", "TerraScale", "bot", "webhook", "context", database, 20_000);
    private static GitHubClient Client(HttpMessageHandler transport, ReviewerOptions options) =>
        new(new HttpClient(transport), options, (_, _, _) => Task.FromResult("installation-test-token"));
    private sealed class ArchiveTransport(byte[] archive) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }
}
