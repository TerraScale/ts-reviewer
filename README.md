# ts-reviewer

`ts-reviewer` is TerraScale's severe, evidence-backed Forgejo pull-request reviewer. It runs only for pull requests targeting `main`, reviews each new head, posts inline findings and a summary, requests changes for critical/high findings, and publishes a required `ts-reviewer` status.

It performs four independent model passes (correctness/data integrity, security, contracts/operations, and tests/adversarial behavior) through the internal CLIProxy endpoint. Findings are deduplicated and low-confidence output is discarded. Verified unrelated defects are filed once in their owning TerraScale repository with a marker for idempotency.

## Runtime

Required environment variables are `TS_REVIEWER_FORGEJO_TOKEN` and `TS_REVIEWER_PROXY_TOKEN` (the proxy only serves the review model). Optional variables include `TS_REVIEWER_LISTEN` (default `http://+:8080`), `TS_REVIEWER_FORGEJO_URL`, `TS_REVIEWER_PROXY_URL`, `TS_REVIEWER_MODEL` (default `glm-5.3-flash`), `TS_REVIEWER_MAX_CONCURRENT` (default `10`), `TS_REVIEWER_TIMEOUT` (default `00:10:00`), and `TS_REVIEWER_WEBHOOK_SECRET`. Configure a Forgejo webhook for push, pull-request, and issue-comment events at `/webhook`.

Every push to a branch queues exact-commit indexing in the embedded LiteDB context database (`TS_REVIEWER_CONTEXT_DATABASE`, default `data/reviewer-context.db`). Search uses LiteDB's built-in vector index (HNSW, cosine), and text embeddings are produced locally by a bundled ONNX model (`SmartComponents.LocalEmbeddings`, 384 dimensions) that is downloaded once at build time — no external embedding service is needed at runtime. Set `TS_REVIEWER_CONTEXT_API_TOKEN` before exposing `/context/search` outside the private network. When the reviewer picks up a pull request or a `@terrascale-bot` command it reacts with 👀 so authors can see it is working.

Never put tokens in the image, workflow YAML, or command-line arguments. The Forgejo token needs pull-request read, review/comment/status write, and issue write access across the TerraScale organization. The proxy token should be reachable only from the homelab network.

## Container release

The service targets .NET 11 and is built from `src/TerraScale.Reviewer/TerraScale.Reviewer.csproj`. Feature slices live below that project directory, while tests are in `tests/TerraScale.Reviewer.Tests/`. Run local checks with `dotnet build src/TerraScale.Reviewer/TerraScale.Reviewer.csproj` and `dotnet test tests/TerraScale.Reviewer.Tests/TerraScale.Reviewer.Tests.csproj`. Build the release image with `dotnet publish src/TerraScale.Reviewer/TerraScale.Reviewer.csproj --configuration Release --os linux --arch x64 -p:LocalRegistry=Podman /t:PublishContainer`.

The context API accepts `POST /context/search` with `{ "repository": "TerraScale/ts-reviewer", "commit": "<sha>", "query": "Forgejo webhook authentication", "top_k": 8 }` and returns source snippets with repository, path, line range, symbol, and score metadata. Reviews automatically wait for the exact PR head to be indexed, then include the retrieved context in every model pass.
Tag PR images by immutable commit SHA. A protected main release may promote the
exact tested digest to the Forgejo container registry; it must not rebuild the
image. The eventual homelab deployment supplies the public webhook URL and
secrets.

Deploying the LiteDB-based image over a pre-LiteDB deployment: mount a
persistent volume at the context database directory (the parent of
`TS_REVIEWER_CONTEXT_DATABASE`, default `data/`) or every restart re-indexes
from scratch, and delete any previous SQLite `reviewer-context.db` file in that
directory first — LiteDB refuses the old file format and the service exits at
startup.
