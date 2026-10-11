# ts-reviewer

`ts-reviewer` reviews TerraScale Forgejo pull requests targeting `main`. It reviews each new head automatically, posts inline findings and a summary, and requests changes with a failed `ts-reviewer` status for any finding. A complete review with no findings submits an approval and a successful status. Failed or incomplete reviews publish an **error** with the reason and submit no verdict, even when some file groups produced findings.

Reviews use [OpenCodeReview](https://github.com/alibaba/open-code-review) (`ocr` 1.12.12, bundled in the image). Each review fetches a blobless partial clone, checks out the exact head, and downloads the blobs required by OCR's merge-base diff before running `ocr review --from <base> --to <head>` with lazy fetching disabled. The image ships git and the static OCR binary; no Node.js is involved.

The fixed model chain is **`glm-5.3` first, `glm-5.3-flash` fallback**, served by ts-router's Anthropic-compatible `/v1/messages` endpoint through CLIProxy/Z.AI. These IDs are present in the router's live `/v1/models` catalog. Explicit model requests bypass the router's automatic model-selection chain. Sol and other models cannot be selected by the reviewer or a comment command.

An authenticated loopback endpoint owns each model call's **30-attempt total budget**. Every step starts on GLM 5.3; after three consecutive failures it switches to Flash, alternating in blocks of three failures until a response succeeds or the budget is exhausted. HTTP failures (including 429 and 5xx), connection errors, twenty-minute request timeouts, empty responses and malformed Anthropic messages or tool calls retry with exponential backoff and jitter, capped at 60 seconds. Responses are buffered and validated before OCR sees them. Tool names and arguments are checked against the request's tool definitions, including the types, required fields, enums and nested arrays used by pinned OCR 1.12.12; stop reasons must agree with tool-use content. OCR 1.12.12 uses the non-streaming Anthropic `Messages.New` API; the local endpoint explicitly rejects `stream: true`. The recorded user decision of 2026-10-10 requires all 30 failed attempts before an upstream error is reported, so authentication and invalid-request responses also retain that budget. Exhaustion returns a non-retryable error to prevent OCR's five hidden SDK retries from multiplying the budget, and makes the entire review fail. Logs identify the PR/head, step, model, attempts, retry count and failure reason.

At most **two PRs run concurrently**, with **one file group at a time in each PR** (`ocr --concurrency 1`). Model IDs, retry budget, backoff, request timeout and these concurrency limits are fixed in code. Each OCR model call has a 10-hour-31-minute SDK deadline: enough for thirty twenty-minute attempts, the maximum retry waits and headroom. File groups have no separate wall-clock deadline (`ocr --timeout 0`); each successive tool interaction retains its complete retry budget. There is no whole-review deadline or timeout configuration that can truncate a large PR or its thirty attempts. Git operations have a fixed twenty-minute deadline. An inactivity watchdog terminates OCR after twenty minutes without a model call and publishes a clear error; it pauses throughout an active model call and its complete retry/backoff budget, then resets between calls. Superseded heads, closed PRs and shutdowns cancel work directly. Comment `@terrascale-bot recheck` to request another review.

## Zero-configuration review policy

Every OCR review receives the TerraScale policy
as review background. New production or CI switches, feature-flag libraries,
opt-in deployment or region gates, tuning configuration and production test
modes require changes. Boolean declarations used as operator on/off configuration
also require changes requested, including hardcoded `const`/`readonly bool`
flags and boolean configuration properties, regardless of their name or
whether the default is `true`. The reviewer follows how the value is supplied
and used; entity state, authorization, consent and data-loss safeguards remain
valid. This semantic check runs even when CI does not scan for switches.
The reviewer preserves the repository's normal OCR
rules and distinguishes policy prose, historical inventories and entity state
from new feature configuration. Only documented vendor-owned names can have
a narrow exception.

OCI Vault automatically creates and delivers production service credentials;
manual secret-copy procedures are retired. Region and service discovery are
automatic. Existing runtime options below describe implementation debt for
removal, rather than setup steps or feature activation instructions.

## Recorded user decisions and large pull requests

Every OCR attempt receives the recorded greenfield and v1-only decisions as
well as the zero-configuration policy. Changes replace the v1 contract directly;
reviews must never request migrations, backfills, shims, compatibility generations
or v2 APIs/schemas. Removing obsolete code and affected old data is intentional;
exact target/ownership checks and destructive-effect authorization still apply.

Ingestion uses the authenticated exact-head clone, with no Forgejo patch download
or patch-size cap. OCR reviews one local file group at a time using the fixed
GLM 5.3 → GLM 5.3 Flash retry policy above. Only paths with findings need local
patches for inline comment positions, retained after full-diff rename detection.
A review that cannot finish publishes an explicit error status with the reason
and submits no verdict, even when some groups produced findings.

## Runtime

Configuration precedence for existing endpoint, credential and context options is defaults < optional JSON config file < environment variables < `--key=value` arguments. Select the config file with `--config=/path/reviewer.json` or `TS_REVIEWER_CONFIG`; the argument takes precedence. It is a flat JSON object with kebab-case keys and string or number values. Missing, malformed or unknown explicitly selected settings fail startup. Models, retries, concurrency and whole-review timeouts have no configuration keys; retired environment overrides have no effect.

Automatic credential delivery supplies `TS_REVIEWER_FORGEJO_TOKEN`, `TS_REVIEWER_PROXY_TOKEN`, `TS_REVIEWER_WEBHOOK_SECRET`, `TS_REVIEWER_CONTEXT_API_TOKEN` and the separate `TS_REVIEWER_OPERATOR_TOKEN`. Existing endpoint settings are `TS_REVIEWER_LISTEN` (default `http://+:8080`), `TS_REVIEWER_FORGEJO_URL` and `TS_REVIEWER_PROXY_URL` (default `http://10.11.99.1:8318`, ts-router). Context indexing retains its existing options below.

Accepted reviews publish a pending `ts-reviewer` status (`Review queued`, then `Review in progress`). Each pull request has at most one queued and one running review: a newer request replaces the queued one, a push cancels the running review of the old head (even when another request is already queued), a request for the head already under review is dropped, and closing the pull request cancels its work. A head whose review is dropped this way gets a `skipped` `ts-reviewer` status (`Review skipped: a newer push replaced this head` or `…: the pull request was closed`) instead of keeping a pending one forever; a running review is marked only once it has stopped, and a shutdown marks nothing so startup recovery still finds the pending head. Queued work waits for a worker without expiring; running reviews retain independent operation budgets until completion or queue cancellation. On startup the reviewer queues every open pull request whose head still shows a pending `ts-reviewer` status or has none at all, so a restart neither strands accepted reviews nor misses pushes delivered while it was down. Comments written by the bot never trigger a review. Configure a Forgejo webhook for push, pull-request, and issue-comment events at `/webhook`.

TS-585 explicitly overrides the all-data persistence rule for transient review scheduling: the review queue and per-call retry state live only in memory and reset on restart. Startup recovery uses Forgejo pull-request statuses to queue unfinished heads again. The durable LiteDB `ContextStore` and context indexing jobs retain their persistence behavior.

Every push to a branch queues exact-commit indexing in a bounded, deduplicated queue backed by the embedded LiteDB context database (`TS_REVIEWER_CONTEXT_DATABASE`, default `data/reviewer-context.db`). The database opens in shared mode for rolling deployments and multiple reviewer processes. Pending jobs are replayed after a process restart and removed only after a successful atomic commit replacement. Failed jobs persist their attempt count, next retry time, and last error; exponential retry is bounded and jobs move to a durable dead-letter state after the configured attempt limit. Search uses LiteDB's built-in vector index (HNSW, cosine), and text embeddings are produced locally by a bundled ONNX model run on an explicit single-threaded onnxruntime session per inference, spread across up to eight inputs at a time (onnxruntime's default pins a thread per host core, which fails inside a CPU-limited container) (`SmartComponents.LocalEmbeddings`, 384 dimensions) that is downloaded once at build time — no external embedding service is needed at runtime. Startup rejects missing or blank authentication secrets. Every webhook requires a valid SHA-256 HMAC in `X-Forgejo-Signature`; every `/context/search` request requires `Authorization: Bearer <context token>`, including on private networks. Supply secrets through the deployment secret store and use distinct, randomly generated values. There is no unauthenticated development mode. When the reviewer picks up a pull request or a `@terrascale-bot` command it reacts with 👀 so authors can see it is working.

Never put tokens in the image, workflow YAML, or command-line arguments. The Forgejo token needs pull-request read, review/comment/status write, and issue write access across the TerraScale organization. The proxy token should be reachable only from the homelab network.

The homelab runs the service on alpha as an Incus OCI instance. OpenBao materializes its credentials into `/var/srv/appdata/ts-reviewer/ts-reviewer.env`, mounted as `/secrets/ts-reviewer.env`; the instance entrypoint sources that file. The existing context-data volume is retained across releases. `homelab-deployer` stages the immutable Forgejo image through Harbor, rebuilds the instance, checks health and rolls back on failure. Its runbooks are `HomeLab/services/ts-reviewer.md` and `HomeLab/services/deployer.md` in the sibling workspace. After a merged commit's CI publishes the image, use `sudo homelab-deployer roll ts-reviewer <sha>` on alpha for a held deployment, or `sudo homelab-deployer release ts-reviewer` to follow green `main` releases. Deployments of other homelab services are separate work.

## Container release

The service targets .NET 11 and is built from `src/TerraScale.Reviewer/TerraScale.Reviewer.csproj`. Feature slices live below that project directory, while tests are in `tests/TerraScale.Reviewer.Tests/`. Run local checks with `dotnet build src/TerraScale.Reviewer/TerraScale.Reviewer.csproj` and `dotnet run --project tests/TerraScale.Reviewer.Tests/TerraScale.Reviewer.Tests.csproj` (TUnit). The release image is built from `Containerfile` on Debian trixie-slim, which adds git and the sha256-pinned static `ocr` binary: first publish a self-contained single-file binary with `dotnet publish src/TerraScale.Reviewer/TerraScale.Reviewer.csproj --configuration Release --runtime linux-x64 --self-contained true --output artifacts/ci/container-publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None`, then run `podman build --platform linux/amd64 --file Containerfile .`.

The context API accepts `POST /context/search` with `{ "repository": "TerraScale/ts-reviewer", "commit": "<sha>", "query": "Forgejo webhook authentication", "top_k": 8 }` and returns source snippets with repository, path, line range, symbol, and cosine-score metadata. Search applies the repository/commit boundary before a bounded exact cosine ranking, preserving tenant/commit isolation while avoiding vector-index writes during request-time reads. Reviews run through ocr and do not read this index; the context API serves other clients. Operators can inspect bounded dead-letter metadata with `GET /admin/context/dead-letter` and replay one job with `POST /admin/context/dead-letter/replay` using the separate operator bearer token; responses never expose lease tokens or indexed source content.
CI (`.forgejo/workflows/ci.yml`) tags the image by immutable commit SHA. Pull
requests only build and inspect it; pushes to `main` push the image tested in
the same job to `github.com/TerraScale/ts-reviewer:<sha>` with the
`TERRASCALE_BOT_TOKEN` organization secret. The homelab deployment supplies the
public webhook URL and secrets.

Deploying the LiteDB-based image over a pre-LiteDB deployment: mount a
persistent volume at the context database directory (the parent of
`TS_REVIEWER_CONTEXT_DATABASE`, default `data/`) or every restart re-indexes
from scratch, and delete any previous SQLite `reviewer-context.db` file in that
directory first — LiteDB refuses the old file format and the service exits at
startup.
