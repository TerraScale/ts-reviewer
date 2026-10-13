# GitHub Actions → Alpha deployment

The main-only `alpha` GitHub environment and $200 monthly spending authorization are established. The user approved the reviewer-only HomeLab transition, first cutover, and scoped CI identity/network integration, conditional on passing auth, billing, artifact, rollback, and CI-access checks. Subsequent trusted `main` pushes deploy automatically; CI and configuration preflight make no model calls.

Reviewer PR #1 and HomeLab PR #1 are merged. Latest main `ab71a4ff9ac3bde8fe011ce634d6da57d4811d90` passed its checks, image publication, and environment gate in [run 38309746061](https://github.com/TerraScale/ts-reviewer/actions/runs/38309746061), but deployment stopped at OpenBao ephemeral SSH signing before Alpha SSH or any restart. The existing Forgejo reviewer remains healthy until every prerequisite below is satisfied.

## Required setup before cutover

1. PR checks run on Alpha's isolated, ephemeral runners after the owner's runner integration in [PR #5](https://github.com/TerraScale/ts-reviewer/pull/5), main commit `ab71a4ff9ac3bde8fe011ce634d6da57d4811d90`. Trusted main/dispatch checks, image publication, environment verification, and deployment stay fixed to clean GitHub-hosted `ubuntu-latest` runners. Shared writable Alpha SDK, NuGet, and BuildKit caches are untrusted; neither they nor PR artifacts enter production builds or package-write jobs. PR jobs receive only a read-only token and cannot reach OpenBao or production SSH. Event-based routing needs no `CI_RUNNER` variable. Preserve the owner's concurrency and timeout changes. Require current-head checks before merging; historical results do not verify a new Alpha PR run.
2. The approved repository environment **`alpha`** is created at `https://github.com/TerraScale/ts-reviewer/settings/environments` with a verified custom deployment branch policy containing exactly the branch `main`. The workflow verifies that policy before its deployment job can run. Protect `main` with **`Check and build ts-reviewer`**. Where `ts-reviewer` is required, pin its source to the reviewer GitHub App so another status writer cannot spoof that check. Further branch-protection changes require approval; the automatic pipeline does not require manual approval for each subsequent push.
3. Complete the [scoped CI identity setup](alpha-ci-auth.md): OpenBao signs a temporary key using a narrowly bound GitHub OIDC role, and a pinned Tailscale action admits an ephemeral network node through federation. Only the deployment job receives `id-token: write`. Its generated `alpha` SSH alias pins the public host identity, uses the dedicated `ts-reviewer-deploy` account, and enforces strict verification. The user's local SSH access does **not** prove hosted Actions access. No operator private key or OAuth secret enters Actions. Existing provider administrators must establish the reviewed signing/federation trust and host account; preparation and approval alone do not prove hosted connectivity. The workflow fails if those capabilities are absent.
4. Complete the GitHub App and Anthropic secure-store setup in [README.md](../README.md). The user approved $200/month and fixed workspace model tiers; enforce it in the Anthropic account/workspace before activating automatic review recovery and deployment. No Anthropic key, App PEM, webhook secret, registry credential, or production configuration is supplied to CI.
5. Apply the reviewed HomeLab changes below only after the approved auth, billing, artifact, rollback, and CI-access conditions are met. They are not performed by this repository's workflow; unrelated service changes remain outside this approval.

The feature branch has no deployment trigger: only trusted pushes to `main` can publish an image and request a protected deployment. The merged main run has not completed a cutover; do not bypass its missing trust, billing, or host prerequisites.

## Required HomeLab transition

The source-only manifest, mounts, persistent-state catalog, and image-roll ownership changes are reviewable in [HomeLab PR #1](https://github.com/MarioGK/Homelab/pull/1). Its shared offline validation passed; no installed declaration or live instance was changed.

Read-only inspection on 2026-10-11 verified these Alpha paths:

| Item | Existing location |
| --- | --- |
| Runtime dotenv materialization | OpenBao `/ci/ts-reviewer`, target `ts-reviewer-env` in `/opt/homelab/openbao/secrets.json` |
| Runtime env mount | `/var/srv/appdata/ts-reviewer/ts-reviewer.env` → `/secrets/ts-reviewer.env`, read-only |
| Persistent context cache | `/var/srv/appdata/ts-reviewer/data` → `/var/lib/ts-reviewer` |
| Incus app and health | `ts-reviewer`, `http://127.0.0.1:3018/healthz` |
| Existing deployment lock | `/run/lock/homelab-deployer.lock` |

The latest read-only Alpha check found the installed `/opt/homelab/deployer/apps.json` enabled, with only `ku` in its `apps` object. `ts-reviewer` is already absent from Forgejo polling; the older checkout and migration-pause notes do not describe current live ownership. Leave `ku` and other applications intact. Before activation, the reviewed HomeLab service runbook and image-roll declaration must record the GitHub pipeline's authority to replace only `ts-reviewer`. The utility uses the existing global lock and refuses a paused deployer or renewed Forgejo ownership. Do not introduce a new reviewer deployment flag.

Keep the existing entrypoint while preparing the migration. The .NET SDK image contains the self-contained executable at `/app/TerraScale.Reviewer`, and its tooling base provides the static `/ts-reviewer` compatibility link. Both the running Forgejo image and the replacement work with the existing entrypoint, which sources `/secrets/ts-reviewer.env` before execution. This avoids changing the running image's executable path before the replacement is viable.

Remove `environment.TS_REVIEWER_MAX_CONCURRENT` from the HomeLab declaration in the reviewed config migration. Runtime settings stay fixed in code. The user has already removed the old Forgejo/proxy fields from OpenBao, so the existing materialized dotenv is the remaining rollback generation: preserve it locally before changing the materialization schema. Do not recreate or retrieve the deleted vendor credentials.

Materialize the GitHub App PEM to **`/var/srv/appdata/ts-reviewer/github-app.pem`** through the approved secret-store mechanism and add a read-only Incus disk device mapping it to **`/secrets/github-app.pem`**. Set **`TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH=/secrets/github-app.pem`** in the runtime dotenv. Both the dotenv and PEM targets are owned by uid/gid 1654 with mode `0600`; both mounts must use `shift = "true"`. The image's `ContainerUser` is 1654, matching the shifted mounts. Preflight checks file metadata before reading configuration or stopping the service. The workflow never creates or copies these credentials. The [prepared target declarations](openbao-ts-reviewer-targets.json) store `TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM` as a raw PEM file, excluding it from dotenv. They are reviewable source only; no installed manifest was changed. The existing `ts-reviewer-env` materialization schema still requires legacy keys and needs an approved HomeLab schema update for the GitHub/Anthropic keys; merely entering an Anthropic key does not complete materialization or App authorization.

Add this device to `tofu/services/ts-reviewer/ts-reviewer.tf`, retaining the existing dotenv and data mounts:

```hcl
device {
  name = "disk_github_app_pem"
  type = "disk"
  properties = {
    source   = "/var/srv/appdata/ts-reviewer/github-app.pem"
    path     = "/secrets/github-app.pem"
    readonly = true
    shift    = "true"
  }
}
```

Prepare and review `python3 tofu/scripts/tofu-on-alpha.py plan` through the HomeLab control plane before any apply. A mount or entrypoint change may stop/restart the service; do not apply it before the replacement and rollback credentials are ready. The deployment preflight reads `incus query /1.0/instances/ts-reviewer`, using `expanded_config` and `expanded_devices`; Alpha's CLI does not support `config show --format=json`.

The complete saved-plan check on 2026-10-12 now passes the reviewer-only allowlist: **0 to add, 1 to change, 0 to destroy**, exit 0. Only `module.ts_reviewer.incus_instance.ts_reviewer` updates. The CI owner's published declarations and the audited backend snapshot at serial **1315** are integrated; the refreshed snapshot is byte-identical to that owner's published snapshot. No targeted plan, state removal, expanded allowlist, or apply was used. Recreate and inspect a fresh saved plan before the approved cutover. The retained builder's retirement remains outside this task.

The OpenBao materializer delivers existing fields; it does **not** generate application credentials. The prepared [C# provisioning utility](../scripts/provision-reviewer-secrets.cs) fills the five verified public identities/paths and generates separate `TS_REVIEWER_WEBHOOK_SECRET`, `TS_REVIEWER_CONTEXT_API_TOKEN`, and `TS_REVIEWER_OPERATOR_TOKEN` on the first GitHub handoff using 256-bit randomness, including replacement of identically named Forgejo-era fields. Store-only `TS_REVIEWER_GITHUB_CREDENTIAL_AUTHORITY` records GitHub App/installation provenance in the same CAS patch; later runs preserve that generation and fill only missing fields. This provenance is excluded from runtime dotenv. Vendor credentials are never changed or resubmitted. Conflicting identities, missing vendor fields, or a concurrent write fail without retry or secret output. The `provision` command does not materialize files, change GitHub settings, start workers, or make model calls.

Preserve the approved original dotenv snapshot on Alpha before the first store write: the scheduled materializer may deliver updated fields automatically. After credential provisioning is authorized, use an existing authenticated operator OpenBao CLI session:

```sh
# Status and pending field names only; no writes or credential generation.
nice -n 19 dotnet run scripts/provision-reviewer-secrets.cs plan
# Approved identity discovery: free token counting, then a CAS patch of the missing workspace ID only.
nice -n 19 dotnet run scripts/provision-reviewer-secrets.cs workspace
# Approved setup, requires an existing authenticated operator: creates separate GitHub credentials on first handoff and fills public fields.
nice -n 19 dotnet run scripts/provision-reviewer-secrets.cs provision
# Approved webhook setup, requires the same operator: reads the store and configures the fixed App hook.
nice -n 19 dotnet run scripts/provision-reviewer-secrets.cs webhook
```

None of these commands accepts credential values. The operator CLI credential stays in its existing secure authentication location. The utility captures OpenBao output in process memory and prints only fixed diagnostics/field names. On 2026-10-12 the approved PC operator session completed `workspace`, `provision`, and `webhook` using the checked main setup binary; all required fields are present and the matching HMAC is configured in GitHub. The temporary operator session was revoked. Alpha's materializer and the development-machine identity retain read-only access; neither was elevated. The `workspace` command needs only the stored inference key, rejects an existing mismatched identity, and never overwrites vendor credentials. The `webhook` operation signs an App JWT in memory and sends the stored HMAC directly to GitHub `PATCH /app/hook/config` with the fixed HTTPS URL, JSON delivery, and certificate verification. Redirects and retries are disabled; no PEM or HMAC is printed or written locally. App/installation authentication, required permissions, and all three event subscriptions passed live status-only checks. Runtime materialization has not changed.

Before replacing the original materialized dotenv, the approved HomeLab secret-delivery transition must preserve its original generation **on Alpha only** at **`/var/srv/appdata/ts-reviewer/rollback/ts-reviewer.env`**, root-owned `0600` in a root-only directory. This contains the running Forgejo image's credentials. Snapshot creation was approved and the root-only Alpha-local copy is verified. Never retrieve its values into this thread or CI. The first deployment refuses to proceed without it. If legacy Vault keys were removed but the original materialized file still exists, snapshot that generation before any rematerialization; if the old credential generation is already unavailable, stop and resolve rollback readiness first. The roll utility restores this original dotenv before rebuilding the original image on a first-migration failure. Subsequent rolls snapshot the current dotenv locally with root-only permissions and restore it before image rollback.

Alpha's installed .NET SDK is 10.0.301; the project uses .NET 11. CI publishes the application, deployment utility, and secure setup utility as self-contained Linux x64 binaries in the checked artifact. An approved operator can run `setup/provision-reviewer-secrets plan`, `workspace`, `provision`, or `webhook` without a .NET SDK on Alpha. These binaries contain no credentials; copying the setup artifact does not authorize its mutating commands. No SDK upgrade is required on Alpha.

## Artifact and deployment guarantees

1. PR checks use a read-only GitHub token. TUnit, dependency audit, migration/deployment fixtures, and the pinned OCR tooling build run before artifacts are accepted. There are no `pull_request_target` jobs and no paid model calls.
2. The trusted `main` image job downloads that run's checked publish directory. `dotnet msbuild /t:PublishContainer -p:PublishDir=<checked-directory>` packages those exact files over the digest-pinned native-tool base from `Containerfile`, without restore, compilation, or a second publish. Images have the full source commit tag and OCI revision label. GHCR publishing uses only the short-lived `GITHUB_TOKEN` with `packages:write` in this job.
3. CI exports that exact digest as an OCI archive. SSH transfers only the image archive and credential-free self-contained application. The root deployment launcher is installed beforehand through the reviewed HomeLab control plane; CI cannot execute an uploaded binary as host root. Alpha uses its existing Harbor auth locally; no registry secret is transferred or logged.
4. Main-only deployments serialize with `cancel-in-progress: false`. A queued commit superseded by newer `main` exits before SSH. Alpha takes the HomeLab global `flock`, verifies migration ownership, secret mounts, entrypoint, OCI commit/digest, and local configuration/PEM without HTTP or model calls. Application preflight executes as uid/gid 1654, with supplementary groups cleared and new privileges prohibited. Preflight failures leave the service running.
5. The utility stages the new full-SHA Harbor tag and a previous-digest rollback tag before stopping. It rebuilds the existing Incus instance, waits up to 120 seconds for `/healthz`, and promotes `:latest` only after health succeeds. A failure rebuilds the previous immutable image and checks its health. Persistent context data stays mounted. GitHub's deployment job reports success/failure; no unsupported GitHub commit-status states are written.

The initial environment/auth/spending setup is a real permission and cost gate. The offline preflight verifies presence and PEM validity; it cannot prove GitHub installation permissions, Anthropic billing authorization, or an agreed spending cap. An authorized operator must verify those before the first deployment. The existing 30-attempt retry policy includes authentication failures and remains unchanged; account for that amplification in the spending decision.

## Recovering an interrupted or failed roll

The utility writes only commit, image digest, rollback tag, rollback dotenv path, and phase to **`/var/lib/ts-reviewer-deploy/state.json`** with root-only permissions. The local credential snapshot is a separate root-only file; no credential values enter state or job output. An interrupted roll or a completed rollback prevents another roll until its outcome is reviewed. The lock is released automatically when the process exits. A killed workflow is not permission to overwrite recovery state.

1. On Alpha, inspect `sudo jq '{Commit,Digest,RollbackTag,RollbackEnv,Phase}' /var/lib/ts-reviewer-deploy/state.json`. This file contains no credentials. Check `curl -fsS http://127.0.0.1:3018/healthz` without printing console logs or environment contents.
2. If replacement health is uncertain, restore **`RollbackEnv`** over `/var/srv/appdata/ts-reviewer/ts-reviewer.env` locally using `sudo install -m 0600 -o 1654 -g 1654 -- <RollbackEnv> /var/srv/appdata/ts-reviewer/ts-reviewer.env`. Both original and replacement images use uid 1654 with a shifted mount; rollback never restores world-readable permissions. Stop the instance if running and rebuild the exact **`RollbackTag`** from that state: `sudo incus rebuild harbor:homelab/ts-reviewer:<RollbackTag> ts-reviewer`, then `sudo incus start ts-reviewer`. Verify `/healthz` before changing `:latest` or retrying. Production recovery and credential restoration need operator approval; do not print either file.
3. After a healthy recovery is confirmed, archive the deployment state and failed job's staged directory. Rerun the deployment job for the current `main` commit; the workflow rechecks freshness and every preflight gate. Preserve the archived state as rollback evidence.

Temporary delivery files live at `/tmp/ts-reviewer-<commit>-<run>-<attempt>`. They contain application artifacts, never production credentials. Rollback images and recovery state are retained for operator review; automatic registry pruning is deferred until retention is agreed.

Verified main checkpoint: [CI run 38251711558](https://github.com/TerraScale/ts-reviewer/actions/runs/38251711558) on `edae11a57e9a9683a903c105cc6a17bda77ef1c2` passed **344/344 tests** (reviewer 284, scanner 13, deployment 14, setup 22, CI SSH 11), zero test skips and zero vulnerable packages. Publish, OCR tooling build, checked artifact delivery, exact OCI image publication, and main-only environment verification passed. Attempts 1 and 2 stopped during CI identity setup before Alpha SSH. The checked launcher also ran its non-mutating usage path on Alpha without requiring a .NET 11 SDK. App authentication and both Anthropic credentials pass; PC workspace/provision/webhook setup is complete. Live cost reports return HTTP 200 with all 11 completed daily buckets and no current-day bucket, even with a wider query. The user approved BILLING-01 option 1 on 2026-10-12: accept latest published costs and delayed downgrades while requiring every completed day; missing identity, authentication, schema or completed-day coverage still stops paid calls. OpenBao's main-only JWT role and ten-minute SSH signer are configured; signer checks accepted the dedicated user with no extensions and rejected root principals and host certificates. Remaining gates: provider-side $200 cap verification, Tailscale federated client/network grants, hosted OIDC failure diagnosis and connectivity, the reviewed Alpha account/root launcher, and HomeLab materializer/PEM mount apply. Public and local legacy health are HTTP 200; no service restart, runtime configuration replacement, or paid inference occurred in these checks.
