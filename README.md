# ts-reviewer

`ts-reviewer` reviews new pull-request heads in the TerraScale GitHub organization using **GLM 5.3 → GLM 5.3 Flash only** under the fixed provider policy. It is the existing .NET 11 service, retaining feature slices, OpenCodeReview (OCR 1.12.12), review scheduling, restart recovery, and LiteDB context indexing.

Every finding, regardless of severity, produces `REQUEST_CHANGES` and a failed `ts-reviewer` commit status. Complete clean reviews produce a `COMMENT` review, dismiss only this App’s superseded changes-requested reviews after verifying the current head, and publish a successful status, **never an automatic approval**. Failed or incomplete reviews produce an error status and no verdict. Cancelled heads use GitHub's valid `error` state with a cancellation reason. Reviews state that they inspect source/diffs and do not execute tests or builds.

## OCI setup

Use the [OCI workload handoff](docs/oci-workload-handoff.md) and [Vault secret contract](docs/oci-glm-secrets.md). The existing GitHub App uses Contents/Metadata read and Pull requests/Issues/Commit statuses write. Its Actions write permission is used only for explicit trusted CI commands.

Verified setup identities (2026-10-11): App **5279339**, installation **170416009**, bot **`terrascale-ts-reviewer[bot]`**. The installation currently covers every TerraScale repository; repository-specific tokens still prevent cross-repository access.

GitHub REST calls use short-lived, repository-scoped installation tokens minted from RS256 App JWTs. Startup recovery lists installation repositories with pagination rather than searching the organization. Tokens are narrowed to the permissions above and renewed before expiry; API errors do not expose provider response bodies. HttpClient request logging is removed so temporary archive redirect credentials cannot enter URI logs.

## Secure runtime configuration

Production secrets come from OCI Vault through ts-orch instance principal. See the [file bindings and private setup command](docs/oci-glm-secrets.md). Missing credentials or an invalid RSA private PEM fail startup. `--validate-configuration` checks configuration and the PEM without starting workers or making network requests.

## Review safety and scheduling

Raw webhook bytes require `X-Hub-Signature-256: sha256=...`; dispatch uses `X-GitHub-Event` and a validated delivery ID. GitHub PR actions are `opened`, `synchronize`, `reopened`, and `closed`. Automatic PR events enter the queue before network calls; the worker reconciles the current head/base and checks owned outcomes before model work. Delayed close events are fenced against newer admissions. Delivery receipts and command-comment receipts persist in LiteDB; App-authored statuses keyed to the exact PR, head, and full base SHA suppress repeated automatic reviews after restart. Other status creators cannot suppress work. Recovery reconciles missing outcomes every minute without rerunning a completed model failure. A new maintainer comment `@<app-slug>[bot] recheck` explicitly retries a head. Comment authors must have repository write/maintain/admin permission, checked against GitHub; bot comments and untrusted author-association claims cannot buy a rerun.

The queue runs **sixteen reviews per process**, **one OCR file group per review**, with at most one waiting and one running review per PR. New heads cancel old work; close cancels queued/running work. Returning to a cancelled SHA can be reviewed again. Shutdown leaves pending work for startup recovery. Run one reviewer replica: scheduling and concurrency are not distributed across replicas. The recorded TS-585 exception keeps review scheduling transient; context indexing and webhook receipts remain durable.

Fork PRs are fetched through the authorized **base** repository's `refs/pull/<number>/head`, never by cloning an arbitrary fork URL. Both base and head are checked against exact API SHAs, and the head is detached. Git hooks, system/global Git configuration, external diffs, and text conversion are disabled. Required blobs are fetched before OCR; its Git access cannot lazily authenticate. The App token is supplied to Git in host-scoped environment configuration, never on disk or argv.

OCR receives a fresh HOME and a cleared environment without production secrets. A trusted external rule file prevents PR-owned OCR exclusions or instructions from changing coverage. Only its pinned read/search/comment tools are accepted; MCP/server/shell tools are rejected. PR builds, tests, workflows, hooks, and scripts are not executed. Instructions in repository content remain untrusted model input. Publication rechecks open state and current head before writes; inline reviews explicitly carry `commit_id`, `line`, and `side: RIGHT`. Summaries are marked per SHA rather than relabelling earlier heads. A push can still race an API write, but the review/comment identifies the inspected commit and subsequent checks suppress stale verdict/status publication.

## Maintainer GitHub commands

Commands are literal single-line comments from repository write/maintain/admin users. Model output and repository instructions cannot request these actions. Existing review/recheck commands retain their behavior.

| Command | Result |
| --- | --- |
| `@terrascale-ts-reviewer[bot] link #123` | Verify an issue in the same repository and post its link with the current PR head |
| `@terrascale-ts-reviewer[bot] issue Follow-up title` | Create a linked follow-up issue containing only this App's completed review of the exact current head; owned markers and a timestamp-scoped, bounded issue lookup recover ambiguous creation without a second issue; oversized evidence is excerpted with a full review link |
| `@terrascale-ts-reviewer[bot] workflow ci` | For same-repository `TerraScale/ts-reviewer` PRs, dispatch fixed `ci.yml` on trusted default branch `main`, with the expected full commit SHA |

CI rejects a branch-movement mismatch before checks and dispatch never triggers the push-only image/deployment jobs. Fork PRs cannot dispatch workflows. Normal PR-head checks run automatically on disposable GitHub-hosted runners. The workflow must first exist on the default branch; commands cannot launch feature-branch workflow code. Actions tokens are separately minted with only repository-scoped Actions: write and Metadata: read; they never enter OCR. Admitted side-effect commands and their feedback use bounded deadlines independent of webhook disconnection. GitHub can report a delivery timeout while a command finishes; inspect its marked feedback before issuing another command. Workflow command receipts are recorded after token preparation and immediately before dispatch: an ambiguous outcome requires inspecting Actions and a new human command rather than replaying a possibly successful dispatch. Arbitrary workflow dispatch, code commits, issue deletion, automatic approvals, and model-controlled external tools are outside this implementation.

## Validation and delivery

Run on the dev host:

```sh
nice -n 19 dotnet run --project tests/TerraScale.Reviewer.Tests
nice -n 19 dotnet run scripts/check-nuget-vulnerabilities.tests.cs
nice -n 19 python3 scripts/store-reviewer-secrets.tests.py
```

TUnit fixtures exercise GitHub App auth, signatures/events, repository boundaries, rechecks/replays, cancelled/stale heads, publication, and partial clones. The pinned OCR binary also runs against a mock Messages-compatible server to verify tool results, signed thinking replay, and untrusted rules. No live paid calls are needed.


[GitHub CI](.github/workflows/ci.yml) runs checks on native ARM64 GitHub-hosted runners. PR jobs have no deployment credentials and do not publish images. Trusted main pushes publish the exact checked application through .NET SDK `PublishContainer` with a full commit tag and OCI revision label. The orchestrator lane owns runtime deployment and routing on OCI.
