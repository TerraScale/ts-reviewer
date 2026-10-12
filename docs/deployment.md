# GitHub → Blacksmith → Alpha deployment

Create the `alpha` GitHub environment only after approving the initial auth and Anthropic spending setup. Subsequent trusted `main` pushes deploy automatically; CI and configuration preflight make no model calls.

The workflow is prepared. It has not deployed or modified Alpha. The existing Forgejo reviewer remains the running service until every prerequisite below is satisfied.

## Required setup before merging

1. The Blacksmith integration and `blacksmith-4vcpu-ubuntu-2404` runner are verified: the draft PR executed tests on this runner. PR jobs need disposable runners without Alpha SSH identities, production-network access, or runtime secrets. Keep production access confined to the protected deployment job.
2. Create repository environment **`alpha`** at `https://github.com/TerraScale/ts-reviewer/settings/environments` with a custom deployment branch policy containing exactly the branch `main`. The workflow verifies that policy before its deployment job can run. Protect `main` with **`Check and build ts-reviewer`**. Where `ts-reviewer` is required, pin its source to the reviewer GitHub App so another status writer cannot spoof that check. These initial GitHub permission changes require user approval; the automatic pipeline does not require manual approval for each subsequent push.
3. Provide the protected deployment job with an existing, isolated Alpha SSH capability: the `alpha` SSH alias must resolve, `BatchMode=yes` and `StrictHostKeyChecking=yes` must succeed, and its identity must have the already configured `sudo -n` capability. The user's local SSH access does **not** prove hosted Blacksmith access. Read-only GitHub checks found no repository secrets, no organization secrets available to this repository, and no `alpha` environment; the workflow does not inject a deployment identity. Do not copy a private key into Actions, use `ssh-keyscan` to trust a new host, or expose a production identity to PR jobs. A new network or identity integration requires a separate approved setup; the workflow presently fails if the existing capability is absent. Organization-wide secret enumeration was denied (HTTP 403); no broader access was requested.
4. Complete the GitHub App and Anthropic secure-store setup in [README.md](../README.md). The user approved $200/month; enforce it in the Anthropic account/workspace before activating automatic review recovery and deployment. No Anthropic key, App PEM, webhook secret, registry credential, or production configuration is supplied to CI.
5. Apply the reviewed HomeLab changes below. They change production config and must be approved before execution. They are not performed by this repository's workflow.

Do not merge this feature branch to trigger an incomplete migration. The feature branch has no deployment trigger: only trusted pushes to `main` can publish an image and request a protected deployment.

## Required HomeLab transition

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

The OpenBao materializer delivers existing fields; it does **not** generate application credentials. The prepared [C# provisioning utility](../scripts/provision-reviewer-secrets.cs) fills the five verified public identities/paths and generates separate `TS_REVIEWER_WEBHOOK_SECRET`, `TS_REVIEWER_CONTEXT_API_TOKEN`, and `TS_REVIEWER_OPERATOR_TOKEN` on the first GitHub handoff using 256-bit randomness, including replacement of identically named Forgejo-era fields. Store-only `TS_REVIEWER_GITHUB_CREDENTIAL_AUTHORITY` records GitHub App/installation provenance in the same CAS patch; later runs preserve that generation and fill only missing fields. This provenance is excluded from runtime dotenv. Vendor credentials are never changed or resubmitted. Conflicting identities, missing vendor fields, or a concurrent write fail without retry or secret output. The `provision` command does not materialize files, change GitHub settings, start workers, or make model calls.

Preserve the approved original dotenv snapshot on Alpha before the first store write: the scheduled materializer may deliver updated fields automatically. After credential provisioning is authorized, use an existing authenticated operator OpenBao CLI session:

```sh
# Status and pending field names only; no writes or credential generation.
nice -n 19 dotnet run scripts/provision-reviewer-secrets.cs plan
# Requires approval: creates separate GitHub credentials on first handoff and fills public fields.
nice -n 19 dotnet run scripts/provision-reviewer-secrets.cs provision
# Requires GitHub webhook-write approval: reads the store and configures the fixed App hook.
nice -n 19 dotnet run scripts/provision-reviewer-secrets.cs webhook
```

None of these commands accepts credential values. The operator CLI credential stays in its existing secure authentication location. The utility captures OpenBao output in process memory and prints only fixed diagnostics/field names; the parent must not run it against the real store without the required authorization. No credential creation or store write has been performed. The `webhook` operation signs an App JWT in memory and sends the stored HMAC directly to GitHub `PATCH /app/hook/config` with the fixed HTTPS URL, JSON delivery, and certificate verification. Redirects and retries are disabled; no PEM or HMAC is printed or written locally. It requires complete GitHub credential provenance. App event subscriptions still need `pull_request`, `issue_comment`, and `push` configured in the App settings on the operator's PC.

Before replacing the original materialized dotenv, the approved HomeLab secret-delivery transition must preserve its original generation **on Alpha only** at **`/var/srv/appdata/ts-reviewer/rollback/ts-reviewer.env`**, root-owned `0600` in a root-only directory. This contains the running Forgejo image's credentials and requires approval as a new persistent secret snapshot. Never retrieve its values into this thread or CI. The first deployment refuses to proceed without it. If legacy Vault keys were removed but the original materialized file still exists, snapshot that generation before any rematerialization; if the old credential generation is already unavailable, stop and resolve rollback readiness first. The roll utility restores this original dotenv before rebuilding the original image on a first-migration failure. Subsequent rolls snapshot the current dotenv locally with root-only permissions and restore it before image rollback.

Alpha's installed .NET SDK is 10.0.301; the project uses .NET 11. CI publishes the application, deployment utility, and secure setup utility as self-contained Linux x64 binaries in the checked artifact. An approved operator can run `setup/provision-reviewer-secrets plan`, `provision`, or `webhook` without a .NET SDK on Alpha. These binaries contain no credentials; copying the setup artifact does not authorize its mutating commands. No SDK upgrade is required on Alpha.

## Artifact and deployment guarantees

1. PR checks use a read-only GitHub token. TUnit, dependency audit, migration/deployment fixtures, and the pinned OCR tooling build run before artifacts are accepted. There are no `pull_request_target` jobs and no paid model calls.
2. The trusted `main` image job downloads that run's checked publish directory. `dotnet msbuild /t:PublishContainer -p:PublishDir=<checked-directory>` packages those exact files over the digest-pinned native-tool base from `Containerfile`, without restore, compilation, or a second publish. Images have the full source commit tag and OCI revision label. GHCR publishing uses only the short-lived `GITHUB_TOKEN` with `packages:write` in this job.
3. CI exports that exact digest as an OCI archive. SSH transfers only the image archive, self-contained preflight binary, and deployment executable. Alpha uses its existing Harbor auth locally; no registry secret is transferred or logged.
4. Main-only deployments serialize with `cancel-in-progress: false`. A queued commit superseded by newer `main` exits before SSH. Alpha takes the HomeLab global `flock`, verifies migration ownership, secret mounts, entrypoint, OCI commit/digest, and local configuration/PEM without HTTP or model calls. Preflight failures leave the service running.
5. The utility stages the new full-SHA Harbor tag and a previous-digest rollback tag before stopping. It rebuilds the existing Incus instance, waits up to 120 seconds for `/healthz`, and promotes `:latest` only after health succeeds. A failure rebuilds the previous immutable image and checks its health. Persistent context data stays mounted. GitHub's deployment job reports success/failure; no unsupported GitHub commit-status states are written.

The initial environment/auth/spending setup is a real permission and cost gate. The offline preflight verifies presence and PEM validity; it cannot prove GitHub installation permissions, Anthropic billing authorization, or an agreed spending cap. An authorized operator must verify those before the first deployment. The existing 30-attempt retry policy includes authentication failures and remains unchanged; account for that amplification in the spending decision.

## Recovering an interrupted or failed roll

The utility writes only commit, image digest, rollback tag, rollback dotenv path, and phase to **`/var/lib/ts-reviewer-deploy/state.json`** with root-only permissions. The local credential snapshot is a separate root-only file; no credential values enter state or job output. An interrupted roll or a completed rollback prevents another roll until its outcome is reviewed. The lock is released automatically when the process exits. A killed workflow is not permission to overwrite recovery state.

1. On Alpha, inspect `sudo jq '{Commit,Digest,RollbackTag,RollbackEnv,Phase}' /var/lib/ts-reviewer-deploy/state.json`. This file contains no credentials. Check `curl -fsS http://127.0.0.1:3018/healthz` without printing console logs or environment contents.
2. If replacement health is uncertain, restore **`RollbackEnv`** over `/var/srv/appdata/ts-reviewer/ts-reviewer.env` locally using `sudo install -m 0600 -o 1654 -g 1654 -- <RollbackEnv> /var/srv/appdata/ts-reviewer/ts-reviewer.env`. Both original and replacement images use uid 1654 with a shifted mount; rollback never restores world-readable permissions. Stop the instance if running and rebuild the exact **`RollbackTag`** from that state: `sudo incus rebuild harbor:homelab/ts-reviewer:<RollbackTag> ts-reviewer`, then `sudo incus start ts-reviewer`. Verify `/healthz` before changing `:latest` or retrying. Production recovery and credential restoration need operator approval; do not print either file.
3. After a healthy recovery is confirmed, archive the deployment state and failed job's staged directory. Rerun the deployment job for the current `main` commit; the workflow rechecks freshness and every preflight gate. Preserve the archived state as rollback evidence.

Temporary delivery files live at `/tmp/ts-reviewer-<commit>-<run>-<attempt>`. They contain application artifacts, never production credentials. Rollback images and recovery state are retained for operator review; automatic registry pruning is deferred until retention is agreed.

Verified checkpoint: [Blacksmith CI on `406c1b9`](https://github.com/TerraScale/ts-reviewer/actions/runs/38188032816) passed all PR stages: reviewer tests **234/234**, scanner fixtures **13/13**, deployment fixtures **9/9**, provisioning/webhook fixtures **10/10**, and **0 vulnerable packages**. Actual OpenBao CLI JSON-stdin/CAS PATCH behavior passed against a local mock. The securely stored App PEM parsed as RSA and authenticated the App/installation (HTTP 200); Anthropic authentication and Opus 5.5 availability passed a read-only request. After the $200/month authorization, one paid Messages smoke request returned HTTP 200 and the expected Opus 5.5 reply: 16 input/4 output tokens, estimated $0.000144 at documented rates. This was provider validation, not an end-to-end reviewer run. Local SDK image revision/digest preservation passed. The Incus JSON query and shifted existing mounts were verified on Alpha; its current service remains healthy (HTTP 200). Not verified: hosted-runner Alpha networking/SSH, protected environment setup, App installation-token minting, production configuration changes, real OCR review inference, or live replacement. The PEM mount, replacement materializer schema, and original rollback snapshot are still absent.
