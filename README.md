# ts-reviewer

`ts-reviewer` is TerraScale's evidence-backed Forgejo pull-request reviewer, with an in-memory weighted scheduler targeting 60% of review starts on GPT-6.1 Sol and 40% on GLM 5.3. It runs only for pull requests targeting `main`, reviews each new head, posts inline findings and a summary, requests changes and fails the required `ts-reviewer` status for any finding regardless of severity, and approves the pull request with a successful status only when there are no findings (so branch protection requiring an approval can merge on a clean review).

Reviews run through [OpenCodeReview](https://github.com/alibaba/open-code-review) (`ocr` 1.12.12, bundled in the image): each review fetches the pull request with the Forgejo token as a blobless partial clone (full history for the merge-base, but no file contents until needed, so a repository with hundreds of MiB of history fetches in about a second), checks out the exact head, downloads the other side of every changed file by running the same merge-base diff ocr runs, and runs `ocr review --from <base> --to <head>` with lazy fetching disabled (ocr's git has no credentials, and everything it reads is already local) with ocr's `OCR_LLM_*` endpoint variables pointed at ts-router's Anthropic-compatible endpoint (which passes requests to CLIProxy) and a generous per-request `OCR_LLM_TIMEOUT` because large file groups take a while (ocr retries each request 5 times on top of that; raising the per-request attempt count further belongs to the proxy's `request-retry`, not ocr, which hardcodes its SDK retry limit). When some file groups still fail, the reviewer resumes ocr's session (`ocr review --resume <session>`): completed items and their findings are reused and only the failed items are reviewed again, with exponential backoff between attempts. Reviews select `gpt-6.1-sol` or `glm-5.3` using weighted admission, with at most two reviews running globally, two Sol slots, and one GLM slot. A full preferred model waits up to two minutes for a slot before spilling to the other model. An HTTP 429 rate limit backs off the affected model and immediately reroutes to the other model when it is available; per-model exponential backoff starts at 30 seconds and caps at ten minutes, resetting after a successful review. When a Sol review still cannot finish after `TS_REVIEWER_OCR_MAX_ATTEMPTS` runs, GLM resumes the same session with its own attempts, so it only reviews the file groups Sol failed and the summary notes that it took over; a GLM review switches to Sol only on a rate limit. The model is passed as ocr's explicit `--model` flag, which is what lets a resumed session switch models. A review without findings passes only when every selected item was reviewed; findings from a partial review still request changes. Error statuses quote the failing command's own error line (ocr's `Error:` or git's `fatal:`), never ocr's JSON usage report. ocr's line-level findings (path, line, severity, suggestion) are mapped to review comments, deduplicated, and posted through the Forgejo API. The image ships git (>= 2.41) and the static `ocr` binary; no Node.js is involved. Comment `@terrascale-bot escalate` on a pull request re-reviews the same head with GPT-6.1 Sol, then the same `glm-5.3` fallback chain.

## Zero-configuration review policy

Every OCR attempt, including a resumed review, receives the TerraScale policy
as review background. New production or CI switches, feature-flag libraries,
opt-in deployment or region gates, tuning configuration and production test
modes require changes. The reviewer preserves the repository's normal OCR
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
or patch-size cap. OCR reviews local file groups and resumes failed groups on the
existing Sol → GLM 5.3 chain. Only paths with findings need local patches for
inline comment positions. A review that cannot finish publishes an explicit error
status; partial findings request changes and cannot approve incomplete coverage.

## Runtime

Configuration precedence is defaults < optional JSON config file < environment variables < `--key=value` arguments. Select the config file with `--config=/path/reviewer.json` or `TS_REVIEWER_CONFIG`; the argument takes precedence. Use a flat JSON object with the existing kebab-case option names, string values for text/durations, and strings or numbers for integer options, for example `{ "max-concurrent": 2, "sol-weight": 60, "glm-weight": 40, "sol-slots": 2, "glm-slots": 1, "model-slot-wait": "00:02:00" }`. Missing, malformed, or invalid explicitly selected files fail startup.

Legacy credential field names (for automatic OCI Vault delivery, not manual setup) are `TS_REVIEWER_FORGEJO_TOKEN`, `TS_REVIEWER_PROXY_TOKEN`, `TS_REVIEWER_WEBHOOK_SECRET`, `TS_REVIEWER_CONTEXT_API_TOKEN`, and the separate `TS_REVIEWER_OPERATOR_TOKEN` (the proxy serves the two review models). Optional variables include `TS_REVIEWER_LISTEN` (default `http://+:8080`), `TS_REVIEWER_FORGEJO_URL`, `TS_REVIEWER_PROXY_URL` (default `http://10.11.99.1:8318`, ts-router), `TS_REVIEWER_MODEL` (default `gpt-6.1-sol`), `TS_REVIEWER_ESCALATION_MODEL` (default `gpt-6.1-sol`), `TS_REVIEWER_FALLBACK_MODEL` (default `glm-5.3`), `TS_REVIEWER_MAX_CONCURRENT` (default `2`, bounded to 1–2 review workers), `TS_REVIEWER_OCR_CONCURRENCY` (default `16`, bounded to 1–64 OCR subtasks per Sol review; GLM always uses one subtask), `TS_REVIEWER_OCR_MAX_ATTEMPTS` (default `3`, bounded to 1–20 ocr runs per model, each resuming the previous one, before the review falls back or is reported incomplete), `TS_REVIEWER_OCR_RETRY_BASE_DELAY` (default `00:00:10`, doubled per retry, capped at ten minutes), `TS_REVIEWER_OCR_LLM_TIMEOUT` (default `00:20:00`, per LLM request, bounded to one minute–two hours), `TS_REVIEWER_OCR_ITEM_TIMEOUT` (default `01:00:00`, per ocr file group, bounded to one minute–24 hours), `TS_REVIEWER_TIMEOUT` (default `04:00:00`, per running review including every retry), `TS_REVIEWER_CONTEXT_MAX_ATTEMPTS` (default `5`), and `TS_REVIEWER_CONTEXT_RETRY_BASE_DELAY` (default `00:00:01`, capped at one minute). `TS_REVIEWER_MODEL` and `TS_REVIEWER_ESCALATION_MODEL` must be exactly `gpt-6.1-sol`; `TS_REVIEWER_FALLBACK_MODEL` must be exactly `glm-5.3`. No other model, including Astra, is allowed. Accepted reviews publish a pending `ts-reviewer` status (`Review queued`, then `Review in progress`). Each pull request has at most one queued and one running review: a newer request replaces the queued one, a push cancels the running review of the old head (even when another request is already queued), a request for the head already under review is dropped, and closing the pull request cancels its work. A head whose review is dropped this way gets a `skipped` `ts-reviewer` status (`Review skipped: a newer push replaced this head` or `…: the pull request was closed`) instead of keeping a pending one forever; a running review is marked only once it has stopped, and a shutdown marks nothing so startup recovery still finds the pending head. Queued work waits for a worker without expiring; `TS_REVIEWER_TIMEOUT` bounds each running review. On startup the reviewer queues every open pull request whose head still shows a pending `ts-reviewer` status or has none at all, so a restart neither strands accepted reviews nor misses pushes delivered while it was down. Comments written by the bot never trigger a review. Configure a Forgejo webhook for push, pull-request, and issue-comment events at `/webhook`.

Scheduler settings are `TS_REVIEWER_SOL_WEIGHT` (default `60`) and `TS_REVIEWER_GLM_WEIGHT` (default `40`), positive integers totaling 100; `TS_REVIEWER_SOL_SLOTS` (default `2`, bounded to 1–2) and `TS_REVIEWER_GLM_SLOTS` (exactly `1`); `TS_REVIEWER_MODEL_SLOT_WAIT` (default `00:02:00`); `TS_REVIEWER_MODEL_BACKOFF_BASE_DELAY` (default `00:00:30`); and `TS_REVIEWER_MODEL_BACKOFF_MAX_DELAY` (default `00:10:00`, at least the base delay). Durations must be positive. The weights target review starts, so slot saturation and rate limits can change the observed split. Scheduler snapshots expose per-model weights, slot limits, starts, completions, in-flight counts, rate-limit counts, consecutive rate limits, and backoff deadlines; structured logs report model activity, rate limits, backoff, and the observed start share. Queue logs report depth and wait time.

TS-585 explicitly overrides the all-data persistence rule for transient review scheduling: the review queue, model slots, weighted counters, and backoff state live only in memory and reset on restart. Startup recovery uses Forgejo pull-request statuses to queue unfinished heads again. The existing durable LiteDB `ContextStore` and context indexing jobs retain their persistence behavior.

Every push to a branch queues exact-commit indexing in a bounded, deduplicated queue backed by the embedded LiteDB context database (`TS_REVIEWER_CONTEXT_DATABASE`, default `data/reviewer-context.db`). The database opens in shared mode for rolling deployments and multiple reviewer processes. Pending jobs are replayed after a process restart and removed only after a successful atomic commit replacement. Failed jobs persist their attempt count, next retry time, and last error; exponential retry is bounded and jobs move to a durable dead-letter state after the configured attempt limit. Search uses LiteDB's built-in vector index (HNSW, cosine), and text embeddings are produced locally by a bundled ONNX model run on an explicit single-threaded onnxruntime session per inference, spread across up to eight inputs at a time (onnxruntime's default pins a thread per host core, which fails inside a CPU-limited container) (`SmartComponents.LocalEmbeddings`, 384 dimensions) that is downloaded once at build time — no external embedding service is needed at runtime. Startup rejects missing or blank authentication secrets. Every webhook requires a valid SHA-256 HMAC in `X-Forgejo-Signature`; every `/context/search` request requires `Authorization: Bearer <context token>`, including on private networks. Supply secrets through the deployment secret store and use distinct, randomly generated values. There is no unauthenticated development mode. When the reviewer picks up a pull request or a `@terrascale-bot` command it reacts with 👀 so authors can see it is working.

Never put tokens in the image, workflow YAML, or command-line arguments. The Forgejo token needs pull-request read, review/comment/status write, and issue write access across the TerraScale organization. The proxy token should be reachable only from the homelab network.

The homelab runs an Incus OCI `ts-reviewer` container on alpha, with OpenBao
credentials mounted from `/var/srv/appdata/ts-reviewer/ts-reviewer.env` and the
context directory mounted at `/var/lib/ts-reviewer`. `homelab-deployer` stages
commit-tagged .NET container images through Harbor and rolls this instance,
checking `/healthz` and rolling back on failure. For an explicitly authorized
branch deployment, publish its tested commit image and run
`sudo homelab-deployer roll ts-reviewer <sha>` on alpha (holds that image until
`release ts-reviewer`). Keep the two-worker limit and existing model chain.
See the homelab `services/ts-reviewer.md` and `services/deployer.md` runbooks.

## Container release

The service targets .NET 11 and is built from `src/TerraScale.Reviewer/TerraScale.Reviewer.csproj`. Feature slices live below that project directory, while tests are in `tests/TerraScale.Reviewer.Tests/`. Run local checks with `dotnet build src/TerraScale.Reviewer/TerraScale.Reviewer.csproj` and `dotnet run --project tests/TerraScale.Reviewer.Tests/TerraScale.Reviewer.Tests.csproj` (TUnit). The release image is built from `Containerfile` on Debian trixie-slim, which adds git and the sha256-pinned static `ocr` binary: first publish a self-contained single-file binary with `dotnet publish src/TerraScale.Reviewer/TerraScale.Reviewer.csproj --configuration Release --runtime linux-x64 --self-contained true --output artifacts/ci/container-publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None`, then run `podman build --platform linux/amd64 --file Containerfile .`.

The context API accepts `POST /context/search` with `{ "repository": "TerraScale/ts-reviewer", "commit": "<sha>", "query": "Forgejo webhook authentication", "top_k": 8 }` and returns source snippets with repository, path, line range, symbol, and cosine-score metadata. Search applies the repository/commit boundary before a bounded exact cosine ranking, preserving tenant/commit isolation while avoiding vector-index writes during request-time reads. Reviews run through ocr and do not read this index; the context API serves other clients. Operators can inspect bounded dead-letter metadata with `GET /admin/context/dead-letter` and replay one job with `POST /admin/context/dead-letter/replay` using the separate operator bearer token; responses never expose lease tokens or indexed source content.
CI (`.forgejo/workflows/ci.yml`) tags the image by immutable commit SHA. Pull
requests only build and inspect it; pushes to `main` push the image tested in
the same job to `git.terrascale.tech/terrascale/ts-reviewer:<sha>` with the
`TERRASCALE_BOT_TOKEN` organization secret. The homelab deployment supplies the
public webhook URL and secrets.

Deploying the LiteDB-based image over a pre-LiteDB deployment: mount a
persistent volume at the context database directory (the parent of
`TS_REVIEWER_CONTEXT_DATABASE`, default `data/`) or every restart re-indexes
from scratch, and delete any previous SQLite `reviewer-context.db` file in that
directory first — LiteDB refuses the old file format and the service exits at
startup.
