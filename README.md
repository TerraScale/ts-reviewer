# ts-reviewer

`ts-reviewer` reviews new pull-request heads in the TerraScale GitHub organization using direct Anthropic **`claude-opus-5-5`** inference. It is the existing .NET 11 service, retaining feature slices, OpenCodeReview (OCR 1.12.12), review scheduling, restart recovery, and LiteDB context indexing.

Every finding, regardless of severity, produces `REQUEST_CHANGES` and a failed `ts-reviewer` commit status. Complete clean reviews produce a `COMMENT` review and successful status, **never an automatic approval**. Failed or incomplete reviews produce an error status and no verdict. Cancelled heads use GitHub's valid `error` state with a cancellation reason. Reviews state that they inspect source/diffs and do not execute tests or builds.

## GitHub setup on your PC

1. [Open the prefilled TerraScale GitHub App registration](https://github.com/organizations/TerraScale/settings/apps/new?name=TerraScale%20TS%20Reviewer&url=https%3A%2F%2Fgithub.com%2FTerraScale%2Fts-reviewer&public=false&webhook_active=true&webhook_url=https%3A%2F%2Fts-reviewer.mariogk.com%2Fwebhook&contents=read&metadata=read&issues=write&pull_requests=write&statuses=write&events%5B%5D=pull_request&events%5B%5D=issue_comment&events%5B%5D=push). Register for TerraScale only. Contents and Metadata are read-only; Pull requests, Issues, and Commit statuses need write access. No Actions, Administration, or Checks permission is required.
2. Set the webhook to `https://ts-reviewer.mariogk.com/webhook`, subscribing to `pull_request`, `issue_comment`, and `push`. Provision its secret securely in the existing secret store and enter it in GitHub directly. Never put credentials in links, chat, Git, command arguments, or Actions PR jobs.
3. [Install the App](https://github.com/organizations/TerraScale/settings/installations) on selected repositories, initially **TerraScale/ts-reviewer**. The App installation is the repository allowlist; fixed TerraScale ownership is checked separately. Its ID and App ID are setup identities, not feature switches.
4. Generate the App private key in GitHub and store it directly in the approved Vault delivery path. Supply the App ID, installation ID, App bot login (`<app-slug>[bot]`), and delivered PEM path through the runtime names below. The PEM must be mounted read-only; the private key is never passed to OCR.
5. Complete the [Alpha delivery checklist](docs/deployment.md) and explicitly approve the spending policy before starting the replacement. Registration, installation, key provisioning, and repository permission changes remain user-owned gates.

GitHub REST calls use short-lived, repository-scoped installation tokens minted from RS256 App JWTs. Startup recovery lists installation repositories with pagination rather than searching the organization. Tokens are narrowed to the permissions above and renewed before expiry; API errors do not expose provider response bodies.

## Secure runtime configuration

Alpha's installed OpenBao manifest identifies **`/ci/ts-reviewer`**, under KV mount **`secret/homelab`** at [bao.mariogk.com](https://bao.mariogk.com), as the current authority. The target `ts-reviewer-env` delivers to `/var/srv/appdata/ts-reviewer/ts-reviewer.env`, mounted at `/secrets/ts-reviewer.env`. OCI Vault remains the required long-term automatic delivery contract; replacing the currently installed OpenBao delivery mechanism is separate infrastructure work. Secret values remain in the store; never copy them into workflow YAML or this repository.

Required runtime names:

| Name | Purpose |
| --- | --- |
| `TS_REVIEWER_ANTHROPIC_API_KEY` | Direct Anthropic vendor-issued key |
| `TS_REVIEWER_GITHUB_APP_ID` / `TS_REVIEWER_GITHUB_INSTALLATION_ID` | App and selected installation identities |
| `TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH` | Read-only mounted RSA private PEM, normally `/secrets/github-app.pem` |
| `TS_REVIEWER_BOT_LOGIN` | GitHub App bot login |
| `TS_REVIEWER_WEBHOOK_SECRET` | SHA-256 webhook HMAC secret |
| `TS_REVIEWER_CONTEXT_API_TOKEN` / `TS_REVIEWER_OPERATOR_TOKEN` | Distinct context API and operator credentials |

The existing first-party credential provisioner must create/deliver webhook, context, and operator secrets automatically. The Anthropic key and GitHub-issued PEM are stored once and delivered through the approved materializer. The installed manifest still requires the retired Forgejo/proxy keys: update its declaration and PEM delivery before materializing the replacement, preserving the running service until the new image is viable. Do not source an env file into a terminal that prints environment variables.

Missing credentials or an unreadable/invalid RSA private PEM fail startup. `--validate-configuration` checks configuration and the PEM without starting workers or making GitHub/Anthropic requests. Production endpoints are fixed at `https://api.github.com`, `https://github.com`, and `https://api.anthropic.com`; the model is fixed in `ReviewProvider.cs`. Retired Forgejo/proxy settings and model, token, iteration, concurrency, or enable/disable settings are not accepted. Existing context storage/indexing options retain their previous behavior.

## Review safety and scheduling

Raw webhook bytes require `X-Hub-Signature-256: sha256=...`; dispatch uses `X-GitHub-Event` and a validated delivery ID. GitHub PR actions are `opened`, `synchronize`, `reopened`, and `closed`. Live metadata rejects delayed stale events. Delivery receipts and command-comment receipts persist in LiteDB; completed commit statuses suppress repeated automatic reviews after restart. A new maintainer comment `@<app-slug>[bot] recheck` explicitly retries a head. Comment authors must have repository write/maintain/admin permission, checked against GitHub; bot comments and untrusted author-association claims cannot buy a rerun.

The existing queue runs **two reviews per process**, **one OCR file group per review**, with at most one waiting and one running review per PR. New heads cancel old work; close cancels queued/running work. Returning to a cancelled SHA can be reviewed again. Shutdown leaves pending work for startup recovery. Run one Alpha reviewer instance initially: scheduling and concurrency are not distributed across replicas. The recorded TS-585 exception keeps review scheduling transient; context indexing and webhook receipts remain durable.

Fork PRs are fetched through the authorized **base** repository's `refs/pull/<number>/head`, never by cloning an arbitrary fork URL. Both base and head are checked against exact API SHAs, and the head is detached. Git hooks, system/global Git configuration, external diffs, and text conversion are disabled. Required blobs are fetched before OCR; its Git access cannot lazily authenticate. The App token is supplied to Git in host-scoped environment configuration, never on disk or argv.

OCR receives a fresh HOME and a cleared environment without production secrets. A trusted external rule file prevents PR-owned OCR exclusions or instructions from changing coverage. Only its pinned read/search/comment tools are accepted; MCP/server/shell tools are rejected. PR builds, tests, workflows, hooks, and scripts are not executed. Instructions in repository content remain untrusted model input. Publication rechecks open state and current head before writes; inline reviews explicitly carry `commit_id`, `line`, and `side: RIGHT`. Summaries are marked per SHA rather than relabelling earlier heads. A push can still race an API write, but the review/comment identifies the inspected commit and subsequent checks suppress stale verdict/status publication.

## Anthropic compatibility and spending gate

The [official Opus 5.5 documentation](https://platform.claude.com/docs/en/models/opus-5-5/overview) specifies always-on adaptive thinking. OCR 1.12.12 uses `anthropic-sdk-go` 1.63.1 with non-streaming Messages calls. The local authenticated provider preserves the existing retry owner while sending directly to Anthropic with `x-api-key` and `anthropic-version`. It converts forced tool selection to `auto`, omits incompatible thinking/sampling overrides, preserves signed/redacted thinking and tool-result history, and clamps oversized output requests to the model's supported 128,000-token API limit. Ordinary OCR calls retain their 16,384 output-token default. That compatibility limit is not a per-review spending cap.

The recorded policy remains **30 attempts per call**, including 401/403, malformed responses, and request failures, with twenty-minute attempts and bounded retry waits. There is no model fallback. Errors are sanitized and exhaustion fails the whole review without a verdict. Cancellation and the inactivity watchdog remain active.

**Before production activation, the user must choose and enforce an Anthropic account/workspace spending limit.** At the documented $4/million input and $20/million output tokens, repeated billable responses, large contexts, and many OCR interactions can be expensive. OCR's pinned defaults allow up to 100 tool requests per task and two review rounds, repeated across groups; there is no aggregate per-review token/iteration or dollar cap. This migration deliberately adds no tuning knobs or switches and does not silently reduce the thirty-attempt decision. Proposed next cost safeguard, requiring a separate explicit user decision, is a fixed aggregate per-review token/iteration budget; malformed billable responses would need to count toward it. Startup recovery may immediately review every missing/pending head, so credentials alone are not spending approval.

## Validation and delivery

Run on the dev host:

```sh
nice -n 19 dotnet run --project tests/TerraScale.Reviewer.Tests
nice -n 19 dotnet run scripts/check-nuget-vulnerabilities.tests.cs
nice -n 19 dotnet run scripts/deploy-alpha.tests.cs
```

TUnit fixtures exercise GitHub App auth, signatures/events, repository boundaries, rechecks/replays, cancelled/stale heads, publication, and partial clones. The pinned OCR binary also runs against a mock Anthropic server to verify tool results, signed thinking replay, and untrusted rules. No live paid calls are needed.

[GitHub CI](.github/workflows/ci.yml) runs on Blacksmith. PR jobs have no deployment credentials and never publish images; main publishes the checked .NET SDK `PublishContainer` image with full commit SHA/digest, then a separate Alpha job transfers a credential-free OCI archive through the existing SSH capability. It checks readiness before service stop and uses health-gated rollback. Read [deployment.md](docs/deployment.md) for exact infrastructure and authorization gates. Do not merge this migration until those gates are resolved.
