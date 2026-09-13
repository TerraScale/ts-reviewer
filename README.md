# ts-reviewer

`ts-reviewer` is TerraScale's severe, evidence-backed Forgejo pull-request reviewer. It runs only for pull requests targeting `main`, reviews each new head, posts inline findings and a summary, requests changes for critical/high findings, and publishes a required `ts-reviewer` status.

It performs four independent model passes (correctness/data integrity, security, contracts/operations, and tests/adversarial behavior) through the internal CLIProxy endpoint. Findings are deduplicated and low-confidence output is discarded. Verified unrelated defects are filed once in their owning TerraScale repository with a marker for idempotency.

## Runtime

Required environment variables are `TS_REVIEWER_FORGEJO_TOKEN` and `TS_REVIEWER_PROXY_TOKEN`. Optional variables include `TS_REVIEWER_LISTEN` (default `http://+:8080`), `TS_REVIEWER_FORGEJO_URL`, `TS_REVIEWER_PROXY_URL`, `TS_REVIEWER_MODEL` (default `glm-5.3-flash`), `TS_REVIEWER_EMBED_MODEL` (default `text-embedding-3-small`), `TS_REVIEWER_MAX_CONCURRENT` (default `10`), `TS_REVIEWER_TIMEOUT` (default `00:10:00`), and `TS_REVIEWER_WEBHOOK_SECRET`. Configure a Forgejo webhook for push, pull-request, and issue-comment events at `/webhook`.

Every push to a branch queues exact-commit indexing in the embedded SQLite context database (`TS_REVIEWER_CONTEXT_DATABASE`, default `data/reviewer-context.db`). The database uses SQLite FTS5 plus the `sqlite-vec` extension for hybrid lexical and embedding search. Set `TS_REVIEWER_CONTEXT_API_TOKEN` before exposing `/context/search` outside the private network. `TS_REVIEWER_CONTEXT_EMBEDDING_DIMENSIONS` must match the selected embedding model (default `1536`).

Never put tokens in the image, workflow YAML, or command-line arguments. The Forgejo token needs pull-request read, review/comment/status write, and issue write access across the TerraScale organization. The proxy token should be reachable only from the homelab network.

## Container release

The service targets .NET 10 and is built with the SDK project. Run local checks with `dotnet build TerraScale.Reviewer.csproj` and `dotnet test Tests/TerraScale.Reviewer.Tests.csproj`. Build the release image with `dotnet publish TerraScale.Reviewer.csproj --configuration Release --os linux --arch x64 -p:LocalRegistry=Podman /t:PublishContainer`.

The context API accepts `POST /context/search` with `{ "repository": "TerraScale/ts-reviewer", "commit": "<sha>", "query": "Forgejo webhook authentication", "top_k": 8 }` and returns source snippets with repository, path, line range, symbol, and score metadata. Reviews automatically wait for the exact PR head to be indexed, then include the retrieved context in every model pass.
Tag PR images by immutable commit SHA. A protected main release may promote the
exact tested digest to the Forgejo container registry; it must not rebuild the
image. The eventual homelab deployment supplies the public webhook URL and
secrets.
