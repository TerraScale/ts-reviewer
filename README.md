# ts-reviewer

`ts-reviewer` is TerraScale's severe, evidence-backed Forgejo pull-request reviewer. It runs only for pull requests targeting `main`, reviews each new head, posts inline findings and a summary, requests changes for critical/high findings, and publishes a required `ts-reviewer` status.

It performs four independent model passes (correctness/data integrity, security, contracts/operations, and tests/adversarial behavior) through the internal CLIProxy endpoint. Findings are deduplicated and low-confidence output is discarded. Verified unrelated defects are filed once in their owning TerraScale repository with a marker for idempotency.

## Runtime

Required environment variables are `TS_REVIEWER_FORGEJO_TOKEN` and `TS_REVIEWER_PROXY_TOKEN`. Optional variables include `TS_REVIEWER_LISTEN` (default `:8080`), `TS_REVIEWER_FORGEJO_URL`, `TS_REVIEWER_PROXY_URL`, `TS_REVIEWER_MODEL` (default `glm-5.3-flash`), `TS_REVIEWER_MAX_CONCURRENT` (default `10`), `TS_REVIEWER_TIMEOUT` (default `10m`), and `TS_REVIEWER_WEBHOOK_SECRET`. Configure a Forgejo webhook for pull-request and issue-comment events at `/webhook`.

Never put tokens in the image, workflow YAML, or command-line arguments. The Forgejo token needs pull-request read, review/comment/status write, and issue write access across the TerraScale organization. The proxy token should be reachable only from the homelab network.

## Container release

Build with `podman build --pull=missing --tag <image>:<commit-sha> -f Containerfile .`.
Tag PR images by immutable commit SHA. A protected main release may promote the
exact tested digest to the Forgejo container registry; it must not rebuild the
image. The eventual homelab deployment supplies the public webhook URL and
secrets.
