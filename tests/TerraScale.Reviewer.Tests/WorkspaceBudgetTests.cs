using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Review;
using TUnit.Assertions;
using TUnit.Core;

public sealed class WorkspaceBudgetTests
{
    [Test]
    [Arguments("0", "claude-opus-5-5")]
    [Arguments("9999.99999", "claude-opus-5-5")]
    [Arguments("10000", "claude-sonnet-5-5")]
    [Arguments("14999.99999", "claude-sonnet-5-5")]
    [Arguments("15000", "claude-haiku-5-5")]
    [Arguments("19999.99999", "claude-haiku-5-5")]
    [Arguments("20000", "blocked")]
    public async Task UsesWorkspaceFractionalCentsAtExactUsdThresholds(string cents, string expected)
    {
        using var fixture = new Fixture();
        fixture.Cents = cents;
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(expected);
        await Assert.That(fixture.ReportCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments("wrkspc_1")]
    [Arguments("wrkspc_default")]
    public async Task CountsOtherAppsInSelectedWorkspaceButNotOtherWorkspaces(string workspace)
    {
        using var fixture = new Fixture(workspace);
        fixture.Cents = "6000.25";
        fixture.Modify = report =>
        {
            var results = report["data"]![0]!["results"]!.AsArray();
            results.Add(Result(workspace == "wrkspc_default" ? null : workspace, "3999.75"));
            results.Add(Result("unrelated_workspace", "999999"));
            if (workspace != "wrkspc_default") results.Add(Result(null, "999999"));
        };
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(WorkspaceBudget.Sonnet);
        await Assert.That(fixture.WorkspaceCalls).IsEqualTo(2);
    }

    [Test]
    public async Task PaginatedDaysCannotOmitCurrentDayOrChangeCentsToDollars()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        fixture.SendReport = request =>
        {
            var second = request.RequestUri!.Query.Contains("page=second%2Bpage", StringComparison.Ordinal);
            return Response(new JsonObject
            {
                ["data"] = new JsonArray(Day(new DateTime(2026, 10, second ? 2 : 1, 0, 0, 0, DateTimeKind.Utc), "wrkspc_default", "5000")),
                ["has_more"] = !second, ["next_page"] = second ? null : "second+page"
            });
        };
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(WorkspaceBudget.Sonnet);
        await Assert.That(fixture.ReportCalls).IsEqualTo(2);
    }

    [Test]
    public async Task SixteenConcurrentCallsShareOneRefreshIncludingCachedFailure()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeSend = async ct => { started.TrySetResult(); await release.Task.WaitAsync(ct); };
        var first = Select(fixture.Budget);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var others = Enumerable.Range(0, 15).Select(_ => Select(fixture.Budget)).ToArray();
        release.SetResult();
        await Task.WhenAll([first, .. others]);
        await Assert.That(fixture.ReportCalls).IsEqualTo(1);
        fixture.Clock.Now += TimeSpan.FromSeconds(60);
        fixture.Status = HttpStatusCode.Forbidden;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Select(fixture.Budget)));
        await Assert.That(fixture.ReportCalls).IsEqualTo(2);
        await Assert.That(await Select(fixture.Budget)).IsEqualTo("blocked");
    }

    [Test]
    public async Task PersistedHighWaterPreventsUpgradesAfterRefreshAndRestartButResetsWithUtcMonth()
    {
        using var fixture = new Fixture();
        fixture.Cents = "15000";
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(WorkspaceBudget.Haiku);
        fixture.Cents = "0";
        fixture.Clock.Now += TimeSpan.FromSeconds(60);
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(WorkspaceBudget.Haiku);
        fixture.Restart();
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(WorkspaceBudget.Haiku);
        fixture.Clock.Now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(ReviewProvider.PrimaryModel);
        await Assert.That(fixture.ReportCalls).IsEqualTo(4);
    }

    [Test]
    public async Task MidnightForcesNewDayCoverageEvenWithinTheRefreshInterval()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = new DateTimeOffset(2026, 10, 1, 23, 59, 59, TimeSpan.Zero);
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(ReviewProvider.PrimaryModel);
        fixture.Cents = "10000";
        fixture.Clock.Now += TimeSpan.FromSeconds(2);
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(WorkspaceBudget.Sonnet);
        await Assert.That(fixture.ReportCalls).IsEqualTo(2);
    }

    [Test]
    public async Task ReportCrossingMonthRolloverCannotAuthorizeOldMonthModel()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.Zero);
        fixture.Modify = _ => { if (fixture.ReportCalls == 1) fixture.Clock.Now += TimeSpan.FromSeconds(2); };
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(ReviewProvider.PrimaryModel);
        await Assert.That(fixture.ReportCalls).IsEqualTo(2);
    }

    [Test]
    [Arguments("missing_today")]
    [Arguments("duplicate_day")]
    [Arguments("partial_day")]
    [Arguments("wrong_currency")]
    [Arguments("numeric_amount")]
    [Arguments("invalid_amount")]
    [Arguments("negative_amount")]
    [Arguments("missing_workspace")]
    [Arguments("missing_has_more")]
    [Arguments("missing_page")]
    [Arguments("repeated_page")]
    public async Task IncompleteOrMalformedReportFailsClosedWithoutLeakingBodies(string corruption)
    {
        using var fixture = new Fixture();
        fixture.Cents = "15000";
        fixture.Modify = report =>
        {
            var bucket = report["data"]![0]!.AsObject();
            var result = bucket["results"]![0]!.AsObject();
            switch (corruption)
            {
                case "missing_today": report["data"] = new JsonArray(); break;
                case "duplicate_day": report["data"]!.AsArray().Add(bucket.DeepClone()); break;
                case "partial_day": bucket["ending_at"] = "2026-10-01T12:00:00Z"; break;
                case "wrong_currency": result["currency"] = "EUR"; break;
                case "numeric_amount": result["amount"] = 10000; break;
                case "invalid_amount": result["amount"] = "synthetic-billing-secret"; break;
                case "negative_amount": result["amount"] = "-100"; break;
                case "missing_workspace": result.Remove("workspace_id"); break;
                case "missing_has_more": report.Remove("has_more"); break;
                case "missing_page": report["has_more"] = true; break;
                case "repeated_page": report["has_more"] = true; report["next_page"] = "same"; report["data"] = new JsonArray(); break;
            }
        };
        await Assert.That(await Select(fixture.Budget)).IsEqualTo("blocked");
        fixture.Modify = null;
        fixture.Clock.Now += TimeSpan.FromSeconds(60);
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(WorkspaceBudget.Haiku);
    }

    [Test]
    [Arguments(401)]
    [Arguments(403)]
    [Arguments(429)]
    [Arguments(500)]
    public async Task BillingFailureNeverFallsBackToCachedOpus(int status)
    {
        using var fixture = new Fixture();
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(ReviewProvider.PrimaryModel);
        fixture.Clock.Now += TimeSpan.FromSeconds(60);
        fixture.Status = (HttpStatusCode)status;
        await Assert.That(await Select(fixture.Budget)).IsEqualTo("blocked");
        await Assert.That(fixture.ReportCalls).IsEqualTo(2);
    }

    [Test]
    [Arguments("different_id")]
    [Arguments("archived")]
    public async Task WorkspaceIdentityMustExistAndBeActive(string invalid)
    {
        using var fixture = new Fixture("wrkspc_1");
        fixture.Workspace = new { id = invalid == "different_id" ? "wrkspc_2" : "wrkspc_1", archived_at = invalid == "archived" ? "2026-10-01" : null };
        await Assert.That(await Select(fixture.Budget)).IsEqualTo("blocked");
        await Assert.That(fixture.ReportCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(null)]
    [Arguments("wrkspc_other")]
    [Arguments("wrkspc_default,wrkspc_default")]
    public async Task FreeIdentityPreflightMustResolveExactWorkspaceBeforeBillingOrPaidCalls(string? workspace)
    {
        using var fixture = new Fixture();
        fixture.InferenceWorkspace = workspace;
        await Assert.That(await Select(fixture.Budget)).IsEqualTo("blocked");
        fixture.InferenceWorkspace = "wrkspc_default";
        fixture.Clock.Now += TimeSpan.FromSeconds(60);
        await Assert.That(await Select(fixture.Budget)).IsEqualTo("blocked");
        await Assert.That(fixture.ReportCalls).IsEqualTo(0);
        await Assert.That(fixture.IdentityCalls).IsEqualTo(1);
    }

    [Test]
    public async Task MissingOrMismatchedMessagesIdentityStopsAllSubsequentCallsEvenWithCachedBudget()
    {
        using var fixture = new Fixture();
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(ReviewProvider.PrimaryModel);
        fixture.Budget.ConfirmWorkspace("wrkspc_default");
        await Assert.That(() => fixture.Budget.ConfirmWorkspace("wrkspc_other")).Throws<InvalidOperationException>();
        await Assert.That(await Select(fixture.Budget)).IsEqualTo("blocked");
        await Assert.That(() => fixture.Budget.ConfirmWorkspace("wrkspc_default")).Throws<InvalidOperationException>();
        await Assert.That(fixture.ReportCalls).IsEqualTo(1);
    }

    [Test]
    public async Task NonDefaultWorkspaceOnLaterListPageKeepsItsOwnCostRows()
    {
        using var fixture = new Fixture("wrkspc_1");
        fixture.Cents = "15000";
        fixture.SendWorkspaceList = request =>
        {
            var second = request.RequestUri!.Query.Contains("after_id=cursor", StringComparison.Ordinal);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                data = new[] { new { id = second ? "wrkspc_1" : "wrkspc_other", archived_at = (string?)null } },
                has_more = !second, last_id = second ? "wrkspc_1" : "cursor"
            }) };
        };
        await Assert.That(await Select(fixture.Budget)).IsEqualTo(WorkspaceBudget.Haiku);
        await Assert.That(fixture.WorkspaceCalls).IsEqualTo(3);
    }

    private static async Task<string> Select(WorkspaceBudget budget)
    {
        try { return await budget.SelectModelAsync(CancellationToken.None); }
        catch (InvalidOperationException ex)
        {
            await Assert.That(ex.Message.Contains("synthetic-billing-secret", StringComparison.Ordinal)).IsFalse();
            return "blocked";
        }
    }

    private static JsonObject Result(string? workspace, string cents) => new()
    { ["workspace_id"] = workspace, ["amount"] = cents, ["currency"] = "USD" };

    private static JsonObject Day(DateTime date, string workspace, string cents) => new()
    {
        ["starting_at"] = date.ToString("O", CultureInfo.InvariantCulture),
        ["ending_at"] = date.AddDays(1).ToString("O", CultureInfo.InvariantCulture),
        ["results"] = new JsonArray(Result(workspace == "wrkspc_default" ? null : workspace, cents))
    };

    private static HttpResponseMessage Response(JsonObject report) => new(HttpStatusCode.OK) { Content = JsonContent.Create(report) };

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "billing-test-" + Guid.NewGuid().ToString("N"));
        private readonly ReviewerOptions _options;
        private readonly HttpClient _http;
        private ContextStore _store;
        internal Clock Clock { get; } = new();
        internal WorkspaceBudget Budget { get; private set; }
        internal int ReportCalls { get; private set; }
        internal int WorkspaceCalls { get; private set; }
        internal string Cents { get; set; } = "0";
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal Action<JsonObject>? Modify { get; set; }
        internal Func<HttpRequestMessage, HttpResponseMessage>? SendReport { get; set; }
        internal Func<CancellationToken, Task>? BeforeSend { get; set; }
        internal object Workspace { get; set; }
        internal string? InferenceWorkspace { get; set; }
        internal Func<HttpRequestMessage, HttpResponseMessage>? SendWorkspaceList { get; set; }
        internal int IdentityCalls { get; private set; }

        internal Fixture(string workspace = "wrkspc_default")
        {
            _options = new("http://+:8080", "https://github.test", "1", "https://anthropic.test", "synthetic-inference-secret",
                "TerraScale", "bot", "webhook", "context", Path.Combine(_root, "context.db"), 100,
                AnthropicAdminApiKey: "synthetic-billing-secret", AnthropicWorkspaceId: workspace);
            _http = new HttpClient(this, disposeHandler: false);
            _store = new ContextStore(_options);
            Budget = new WorkspaceBudget(_options, _http, _store, Clock, NullLogger<WorkspaceBudget>.Instance);
            Workspace = new { id = workspace, archived_at = (string?)null };
            InferenceWorkspace = workspace;
        }

        internal void Restart()
        {
            _store.Dispose();
            _store = new ContextStore(_options);
            Budget = new WorkspaceBudget(_options, _http, _store, Clock, NullLogger<WorkspaceBudget>.Instance);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/messages/count_tokens")
            {
                if (request.Method != HttpMethod.Post || request.Headers.GetValues("x-api-key").Single() != "synthetic-inference-secret"
                    || request.Headers.Contains("anthropic-workspace-id")) throw new Exception("inference credential separation failed");
                IdentityCalls++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { input_tokens = 4 }) };
                if (InferenceWorkspace is not null) response.Headers.Add("anthropic-workspace-id", InferenceWorkspace);
                return response;
            }
            if (request.Method != HttpMethod.Get || request.Headers.GetValues("x-api-key").Single() != "synthetic-billing-secret"
                || request.Headers.GetValues("anthropic-version").Single() != "2023-06-01" || request.Headers.Authorization is not null)
                throw new Exception("billing credential separation failed");
            if (request.RequestUri!.AbsolutePath == "/v1/organizations/workspaces")
            {
                WorkspaceCalls++;
                if (!request.RequestUri.Query.Contains("include_default=false", StringComparison.Ordinal)) throw new Exception("default exclusion missing");
                return SendWorkspaceList is not null ? SendWorkspaceList(request) : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { data = _options.AnthropicWorkspaceId == "wrkspc_default" ? Array.Empty<object>() : new[] { Workspace },
                        has_more = false, first_id = _options.AnthropicWorkspaceId, last_id = _options.AnthropicWorkspaceId })
                };
            }
            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/organizations/workspaces/", StringComparison.Ordinal))
            {
                WorkspaceCalls++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Workspace) };
            }
            if (request.RequestUri.AbsolutePath != "/v1/organizations/cost_report"
                || !request.RequestUri.Query.Contains("group_by%5B%5D=workspace_id", StringComparison.Ordinal))
                throw new Exception("workspace cost grouping missing");
            ReportCalls++;
            if (BeforeSend is not null) await BeforeSend(ct);
            if (Status != HttpStatusCode.OK) return new(Status) { Content = new StringContent("synthetic-billing-secret") };
            if (SendReport is not null) return SendReport(request);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var start = DateTime.Parse(query.Split("starting_at=")[1].Split('&')[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            var until = DateTime.Parse(query.Split("ending_at=")[1].Split('&')[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).AddDays(-1).Date;
            var data = new JsonArray();
            for (var date = start; date <= until; date = date.AddDays(1)) data.Add(Day(date, _options.AnthropicWorkspaceId, date == start ? Cents : "0"));
            var report = new JsonObject { ["data"] = data, ["has_more"] = false, ["next_page"] = null };
            Modify?.Invoke(report);
            return Response(report);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _http.Dispose(); _store.Dispose(); Directory.Delete(_root, recursive: true); }
            base.Dispose(disposing);
        }
    }
}
