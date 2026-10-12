# ts-reviewer

`ts-reviewer` reviews new pull-request heads in the TerraScale GitHub organization using direct Anthropic inference: **Opus 5.5 → Sonnet 5.5 → Haiku 5.5** at fixed workspace spending thresholds. It is the existing .NET 11 service, retaining feature slices, OpenCodeReview (OCR 1.12.12), review scheduling, restart recovery, and LiteDB context indexing.

Every finding, regardless of severity, produces `REQUEST_CHANGES` and a failed `ts-reviewer` commit status. Complete clean reviews produce a `COMMENT` review, dismiss only this App’s superseded changes-requested reviews after verifying the current head, and publish a successful status, **never an automatic approval**. Failed or incomplete reviews produce an error status and no verdict. Cancelled heads use GitHub's valid `error` state with a cancellation reason. Reviews state that they inspect source/diffs and do not execute tests or builds.

## GitHub setup on your PC

1. [Open the prefilled TerraScale GitHub App registration](https://github.com/organizations/TerraScale/settings/apps/new?name=TerraScale%20TS%20Reviewer&url=https%3A%2F%2Fgithub.com%2FTerraScale%2Fts-reviewer&public=false&webhook_active=true&webhook_url=https%3A%2F%2Fts-reviewer.mariogk.com%2Fwebhook&contents=read&metadata=read&issues=write&pull_requests=write&statuses=write&events%5B%5D=pull_request&events%5B%5D=issue_comment&events%5B%5D=push). Register for TerraScale only. Contents and Metadata are read-only; Pull requests, Issues, and Commit statuses need write access. Actions: write is used only by explicit trusted CI commands. Administration and Checks permissions are unnecessary. The current App already has Actions: write.
2. Subscribe to `pull_request`, `issue_comment`, and `push`. After the secure handoff is authorized, the [setup utility](scripts/provision-reviewer-secrets.cs) configures the webhook URL `https://ts-reviewer.mariogk.com/webhook` and HMAC directly from OpenBao through the App-authenticated GitHub API. Never put credentials in links, chat, Git, command arguments, or Actions PR jobs.
3. [Install the App](https://github.com/organizations/TerraScale/settings/installations) on selected repositories, initially **TerraScale/ts-reviewer**. The App installation is the repository allowlist; fixed TerraScale ownership is checked separately. Its ID and App ID are setup identities, not feature switches.
4. Generate the App private key in GitHub and store it directly in `TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM`. The setup utility fills the App ID, installation ID, bot login, and PEM path. The PEM must be mounted read-only; the private key is never passed to OCR.
5. Complete the [Alpha delivery checklist](docs/deployment.md) before starting the replacement. The $200/month automatic-review budget, secure setup, scoped CI integration, and reviewer-only HomeLab cutover are approved, conditional on successful auth, billing, artifact, rollback, and CI-access checks.

Verified setup identities (2026-10-11): App **5279339**, installation **170416009**, bot **`terrascale-ts-reviewer[bot]`**. The installation currently covers every TerraScale repository; repository-specific tokens still prevent cross-repository access. Blacksmith is installed and has executed the PR checks.

GitHub REST calls use short-lived, repository-scoped installation tokens minted from RS256 App JWTs. Startup recovery lists installation repositories with pagination rather than searching the organization. Tokens are narrowed to the permissions above and renewed before expiry; API errors do not expose provider response bodies. HttpClient request logging is removed so temporary archive redirect credentials cannot enter URI logs.

## Secure runtime configuration

Alpha's installed OpenBao manifest identifies **`/ci/ts-reviewer`**, under KV mount **`secret/homelab`** at [bao.mariogk.com](https://bao.mariogk.com), as the current authority. The target `ts-reviewer-env` delivers to `/var/srv/appdata/ts-reviewer/ts-reviewer.env`, mounted at `/secrets/ts-reviewer.env`. OCI Vault remains the required long-term automatic delivery contract; replacing the currently installed OpenBao delivery mechanism is separate infrastructure work. Secret values remain in the store; never copy them into workflow YAML or this repository.

On your PC, open OpenBao → **Secrets** → **`secret/homelab`** → **`ci/ts-reviewer`** → **Edit**. Enter the Anthropic key in `TS_REVIEWER_ANTHROPIC_API_KEY` and the GitHub-issued private PEM in `TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM` directly there. The setup utility fills the verified public identities/paths. The stored PEM has passed an RSA parse and HTTP 200 App/installation authentication checks without leaving Alpha; its file has not yet been materialized. After the user's $200/month authorization, the Anthropic key passed one bounded paid Messages smoke test with `claude-opus-5-5` (HTTP 200, 16 input/4 output tokens); this was provider validation, not a real PR review. The PEM field is the proposed raw-file delivery contract in [the reviewed manifest fragment](docs/openbao-ts-reviewer-targets.json), not a runtime environment variable. Both delivered files require uid/gid 1654, mode `0600`, and read-only shifted mounts. Never paste credentials into chat.

For whole-workspace monthly model selection, open [Claude Console](https://platform.claude.com) → **Settings** → **Admin keys** on your PC, create the vendor-issued billing credential there, and enter it directly into **`TS_REVIEWER_ANTHROPIC_ADMIN_API_KEY`** in the same OpenBao record. Use the setup utility's **`workspace`** command from an existing authorized OpenBao operator session to discover the inference key's matching **`wrkspc_…`** ID through free token counting and save **`TS_REVIEWER_ANTHROPIC_WORKSPACE_ID`** directly, without displaying or copying its value. The ordinary inference key cannot read billing. Admin keys have organization-wide write authority and cannot be narrowed to cost-only access; an existing org-scoped personal/service-account credential is another documented option, but billing-role access has not been verified. Never enter the billing credential into the inference field. Status-only validation on 2026-10-12 authenticated both Anthropic credentials and confirmed the inference workspace is active and matches the Admin identity. The approved PC operator session completed `workspace`, `provision`, and `webhook`; every required field is now present. Alpha and development-machine identities retain read-only access. Runtime materialization and cutover remain pending. Keep the provider-side $200 limit in this same workspace; delayed billing cannot replace it.

Required runtime names:

| Name | Purpose |
| --- | --- |
| `TS_REVIEWER_ANTHROPIC_API_KEY` | Direct Anthropic inference key |
| `TS_REVIEWER_ANTHROPIC_ADMIN_API_KEY` | Separate vendor-issued billing credential; never supplied to OCR |
| `TS_REVIEWER_ANTHROPIC_WORKSPACE_ID` | Provider-issued `wrkspc_…` identity containing the inference key, including the default workspace |
| `TS_REVIEWER_GITHUB_APP_ID` / `TS_REVIEWER_GITHUB_INSTALLATION_ID` | App and selected installation identities |
| `TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH` | Read-only mounted RSA private PEM, normally `/secrets/github-app.pem` |
| `TS_REVIEWER_BOT_LOGIN` | GitHub App bot login |
| `TS_REVIEWER_WEBHOOK_SECRET` | SHA-256 webhook HMAC secret |
| `TS_REVIEWER_CONTEXT_API_TOKEN` / `TS_REVIEWER_OPERATOR_TOKEN` | Distinct context API and operator credentials |

Public setup values for the verified installation:

```text
TS_REVIEWER_GITHUB_APP_ID=5279339
TS_REVIEWER_GITHUB_INSTALLATION_ID=170416009
TS_REVIEWER_BOT_LOGIN=terrascale-ts-reviewer[bot]
TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH=/secrets/github-app.pem
TS_REVIEWER_CONTEXT_DATABASE=/var/lib/ts-reviewer/reviewer-context.db
```

These identities/paths still need secure-store configuration. The installed materializer only delivers existing fields; the prepared [provisioning utility](scripts/provision-reviewer-secrets.cs) fills public identities/paths and generates separate GitHub webhook, context, and operator credentials on the first handoff. Store-only `TS_REVIEWER_GITHUB_CREDENTIAL_AUTHORITY` records their GitHub provenance; later runs preserve that generation and fill only missing fields. Vendor credentials are always preserved. It requires an authorized operator identity and has not run against production. The approved original Alpha dotenv snapshot is preserved locally, root-owned `0600`, before any store write because the materializer timer can deliver updated fields automatically. The Anthropic key and GitHub-issued PEM are stored once and delivered through the approved materializer. The installed manifest still requires the retired Forgejo/proxy keys: update its declaration and PEM delivery before materializing the replacement, preserving the running service until the new image is viable. Do not source an env file into a terminal that prints environment variables.

Missing credentials or an unreadable/invalid RSA private PEM fail startup. `--validate-configuration` checks configuration and the PEM without starting workers or making GitHub/Anthropic requests. Production endpoints are fixed at `https://api.github.com`, `https://github.com`, and `https://api.anthropic.com`; the monthly model policy is fixed in `WorkspaceBudget.cs` and `ReviewProvider.cs`. Retired Forgejo/proxy settings and model, token, iteration, concurrency, context-budget/retry, or enable/disable settings are not accepted. Context indexing uses fixed 20,000-file and 512-MiB decompression limits, five attempts, and one-second initial backoff; storage paths remain infrastructure identities.

## Review safety and scheduling

Raw webhook bytes require `X-Hub-Signature-256: sha256=...`; dispatch uses `X-GitHub-Event` and a validated delivery ID. GitHub PR actions are `opened`, `synchronize`, `reopened`, and `closed`. Automatic PR events enter the queue before network calls; the worker reconciles the current head/base and checks owned outcomes before model work. Delayed close events are fenced against newer admissions. Delivery receipts and command-comment receipts persist in LiteDB; App-authored statuses keyed to the exact PR, head, and full base SHA suppress repeated automatic reviews after restart. Other status creators cannot suppress work. Recovery reconciles missing outcomes every minute without rerunning a completed model failure. A new maintainer comment `@<app-slug>[bot] recheck` explicitly retries a head. Comment authors must have repository write/maintain/admin permission, checked against GitHub; bot comments and untrusted author-association claims cannot buy a rerun.

The queue runs **sixteen reviews per process**, **one OCR file group per review**, with at most one waiting and one running review per PR. New heads cancel old work; close cancels queued/running work. Returning to a cancelled SHA can be reviewed again. Shutdown leaves pending work for startup recovery. Run one Alpha reviewer instance initially: scheduling and concurrency are not distributed across replicas. The recorded TS-585 exception keeps review scheduling transient; context indexing and webhook receipts remain durable.

Fork PRs are fetched through the authorized **base** repository's `refs/pull/<number>/head`, never by cloning an arbitrary fork URL. Both base and head are checked against exact API SHAs, and the head is detached. Git hooks, system/global Git configuration, external diffs, and text conversion are disabled. Required blobs are fetched before OCR; its Git access cannot lazily authenticate. The App token is supplied to Git in host-scoped environment configuration, never on disk or argv.

OCR receives a fresh HOME and a cleared environment without production secrets. A trusted external rule file prevents PR-owned OCR exclusions or instructions from changing coverage. Only its pinned read/search/comment tools are accepted; MCP/server/shell tools are rejected. PR builds, tests, workflows, hooks, and scripts are not executed. Instructions in repository content remain untrusted model input. Publication rechecks open state and current head before writes; inline reviews explicitly carry `commit_id`, `line`, and `side: RIGHT`. Summaries are marked per SHA rather than relabelling earlier heads. A push can still race an API write, but the review/comment identifies the inspected commit and subsequent checks suppress stale verdict/status publication.

## Maintainer GitHub commands

Commands are literal single-line comments from repository write/maintain/admin users. Model output and repository instructions cannot request these actions. Existing review/recheck commands retain their behavior.

| Command | Result |
| --- | --- |
| `@terrascale-ts-reviewer[bot] link #123` | Verify an issue in the same repository and post its link with the current PR head |
| `@terrascale-ts-reviewer[bot] issue Follow-up title` | Create a linked follow-up issue containing only this App's completed review of the exact current head; owned markers and a timestamp-scoped, bounded issue lookup recover ambiguous creation without a second issue; oversized evidence is excerpted with a full review link |
| `@terrascale-ts-reviewer[bot] workflow ci` | For same-repository `TerraScale/ts-reviewer` PRs, dispatch fixed `ci.yml` on trusted default branch `main`, with the expected full commit SHA |

CI rejects a branch-movement mismatch before checks and dispatch never triggers the push-only image/deployment jobs. Fork PRs cannot dispatch workflows. Normal PR-head checks run automatically on disposable Blacksmith runners. The workflow must first exist on the default branch; commands cannot launch feature-branch workflow code. Actions tokens are separately minted with only repository-scoped Actions: write and Metadata: read; they never enter OCR. Admitted side-effect commands and their feedback use bounded deadlines independent of webhook disconnection. GitHub can report a delivery timeout while a command finishes; inspect its marked feedback before issuing another command. Workflow command receipts are recorded after token preparation and immediately before dispatch: an ambiguous outcome requires inspecting Actions and a new human command rather than replaying a possibly successful dispatch. Arbitrary workflow dispatch, code commits, issue deletion, automatic approvals, and model-controlled external tools are outside this implementation.

## Anthropic compatibility and spending gate

The [official Opus 5.5 documentation](https://platform.claude.com/docs/en/models/opus-5-5/overview) specifies always-on adaptive thinking. OCR 1.12.12 uses `anthropic-sdk-go` 1.63.1 with non-streaming Messages calls. The local authenticated provider preserves the existing retry owner while sending directly to Anthropic with `x-api-key` and `anthropic-version`. It converts forced tool selection to `auto`, omits incompatible thinking/sampling overrides, preserves signed/redacted thinking and tool-result history, and clamps oversized output requests to the model's supported 128,000-token API limit. Ordinary OCR calls retain their 16,384 output-token default. That compatibility limit is not a per-review spending cap.

After the $200/month authorization, one paid Anthropic Messages smoke test returned HTTP 200 with `claude-opus-5-5` and the expected reply: 16 input/4 output tokens, estimated **$0.000144** at the documented rates. It did not run OCR on a PR or activate Alpha.

The recorded policy remains **30 attempts per call**, including 401/403, malformed responses, and request failures, with twenty-minute attempts and bounded retry waits. Automatic tier selection follows the fixed monthly workspace policy below; there is no error-triggered model fallback. Errors are sanitized and exhaustion fails the whole review without a verdict. Cancellation and the inactivity watchdog remain active. Observable local response-delivery failures latch the spent budget before another call can begin; successful socket buffering alone does not prove that OCR consumed a response.

**The user approved $200/month and automatic reviews within it on 2026-10-11.** The later spending policy uses the **entire selected workspace’s reported costs**, including other applications, rather than this reviewer’s token usage:

| Reported UTC-month spend | Model/action |
| --- | --- |
| Below $100 | `claude-opus-5-5` |
| $100 to below $150 | `claude-sonnet-5-5` |
| $150 to below $200 | `claude-haiku-5-5` |
| At least $200 | Fail without a verdict; no further paid call |

All three IDs were verified in the authenticated model-list response. Before OCR starts and before each model attempt, the provider consults a shared workspace budget service. It reads the [official Admin cost-report API](https://platform.claude.com/docs/en/api/beta/organization/cost_report/retrieve), groups by workspace, paginates UTC daily buckets from month start, requires every completed day through yesterday, includes today when published, and parses fractional cents as invariant decimal USD. Every workspace requires a matching active identity response. A paginated list that excludes the default identifies whether its billing rows use the real ID or `null`; this prevents a real default ID from silently counting zero. Before reading costs, a free `count_tokens` preflight authenticates the inference key and requires its server-returned `anthropic-workspace-id` to match. Successful Messages responses must confirm that same identity. Missing/mismatched identity latches a process-wide stop for new model calls, with no verdict or automatic retry; repair secure configuration before restarting. Selected-workspace totals are shared for at most 60 seconds. A persisted monthly high-water mark prevents stale/lower reports or restarts from upgrading the tier. UTC month rollover resets selection. Signed thinking and tool-result history are passed unchanged across tier changes; comments name models actually attempted. Billing failures log one fixed reason per refresh (HTTP status, coverage counts, or schema category) without response bodies, credentials or workspace header values.

**On 2026-10-12 the user approved BILLING-01 option 1: use latest published whole-workspace costs despite missing current-day costs and delayed downgrades.** The live contract check returned HTTP 200 with all 11 completed daily buckets and no current-day bucket, even with a wider query. Only today may be absent; missing earlier days, missing billing credentials, inference-workspace binding failure, authentication failure, malformed/non-USD reports, or reaching the reported $200 total still stops the review without a verdict. Unpublished spending remains unknown. On the first UTC day of a month, a valid empty current-month report starts the new month at its published total of zero; prior-month high-water marks do not carry over. Billing reads remain separate from the preserved 30 model attempts. The request includes today when available. The free token-count endpoint authenticated and returned a matching workspace identity; no paid call was used for these checks. The remaining deployment/auth gates still apply.

**This is a delayed downgrade policy, not a hard spending limiter.** The [official usage/cost docs](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) say costs typically arrive within five minutes, sometimes later. Sixteen concurrent reviews and already in-flight calls can cross thresholds during that delay. Cost reports exclude Priority Tier commitments and traffic billed through other cloud providers; the policy covers whole-workspace costs represented by the direct Anthropic report, not those excluded invoice items. Confirm the workspace has no excluded spending before treating it as whole-workspace accounting. Keep the provider-side $200 account/workspace limit in force; its enforcement has not been independently verified.

Refusal and max-token responses can be billed and retried under the recorded 30-attempt policy, including 401/403. At Opus’s documented $20/million output rate, 30 responses at the 128,000 output-token compatibility ceiling represent **$76.80 output alone for one step**, before input. OCR’s pinned defaults allow up to 100 tool requests per task and two rounds across groups. There is no aggregate per-review token/iteration limit. The fixed tiers and 16-worker limit add no tuning knobs or switches. A separate fixed aggregate cap would require another user decision.


## Validation and delivery

Run on the dev host:

```sh
nice -n 19 dotnet run --project tests/TerraScale.Reviewer.Tests
nice -n 19 dotnet run scripts/check-nuget-vulnerabilities.tests.cs
nice -n 19 dotnet run scripts/deploy-alpha.tests.cs
nice -n 19 dotnet run scripts/provision-reviewer-secrets.tests.cs
```

TUnit fixtures exercise GitHub App auth, signatures/events, repository boundaries, rechecks/replays, cancelled/stale heads, publication, and partial clones. The pinned OCR binary also runs against a mock Anthropic server to verify tool results, signed thinking replay, and untrusted rules. No live paid calls are needed.

Verified pushed checkpoint [`708c306`](https://github.com/TerraScale/ts-reviewer/commit/708c306f6b11ad1ef22b0b6c8449c1eaa62b3bff): **234/234** reviewer tests, **13/13** scanner fixtures, **11/11** deployment fixtures, **10/10** provisioning/webhook fixtures, no skips, and **0** vulnerable packages. [Hosted Blacksmith CI](https://github.com/TerraScale/ts-reviewer/actions/runs/38188528580) passed the tests, audit, application/deployer publish, pinned OCR tooling build, and artifact upload. PRs do not publish registry images or deploy Alpha.

[GitHub CI](.github/workflows/ci.yml) runs on Blacksmith. PR jobs have no deployment credentials and never publish images; main publishes the checked .NET SDK `PublishContainer` image with full commit SHA/digest, then a separate Alpha job transfers a credential-free OCI archive through the existing SSH capability. It checks readiness before service stop and uses health-gated rollback. Read [deployment.md](docs/deployment.md) for exact infrastructure and authorization gates. Do not merge this migration until those gates are resolved.
