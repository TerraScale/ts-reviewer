# GitHub → Blacksmith → Alpha deployment

Create the `alpha` GitHub environment only after approving the initial auth and Anthropic spending setup. Subsequent trusted `main` pushes deploy automatically; CI and configuration preflight make no model calls.

The workflow is prepared. It has not deployed or modified Alpha. The existing Forgejo reviewer remains the running service until every prerequisite below is satisfied.

## Required setup before merging

1. The Blacksmith integration and `blacksmith-4vcpu-ubuntu-2404` runner are verified: the draft PR executed tests on this runner. PR jobs need disposable runners without Alpha SSH identities, production-network access, or runtime secrets. Keep production access confined to the protected deployment job.
2. Create repository environment **`alpha`** at `https://github.com/TerraScale/ts-reviewer/settings/environments` with a custom deployment branch policy containing exactly the branch `main`. The workflow verifies that policy before its deployment job can run. Protect `main` with **`Check and build ts-reviewer`**. Where `ts-reviewer` is required, pin its source to the reviewer GitHub App so another status writer cannot spoof that check. These initial GitHub permission changes require user approval; the automatic pipeline does not require manual approval for each subsequent push.
3. Provide the protected deployment job with an existing, isolated Alpha SSH capability: the `alpha` SSH alias must resolve, `BatchMode=yes` and `StrictHostKeyChecking=yes` must succeed, and its identity must have the already configured `sudo -n` capability. The user's local SSH access does **not** prove hosted Blacksmith access. Do not copy a private key into Actions, use `ssh-keyscan` to trust a new host, or expose a production identity to PR jobs. A new network or identity integration requires a separate approved setup; the workflow presently fails if the existing capability is absent.
4. Complete the GitHub App and Anthropic secure-store setup in [README.md](../README.md). Approve an Anthropic account/workspace spending cap before activating automatic review recovery and deployment. No Anthropic key, App PEM, webhook secret, registry credential, or production configuration is supplied to CI.
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

The installed `/opt/homelab/deployer/apps.json` explicitly pauses image rolls because Forgejo is retained as a backup while GitHub delivery is migrated. The new deployment utility honors that existing policy. It also refuses to roll while `ts-reviewer` remains owned by the Forgejo poller. A reviewed HomeLab change must transfer this app's image ownership to the GitHub pipeline and resolve that pause without reactivating unintended Forgejo deployments. Leave other applications' ownership intact. Do not introduce a new reviewer deployment flag.

Keep the existing entrypoint while preparing the migration. The .NET SDK image contains the self-contained executable at `/app/TerraScale.Reviewer`, and its tooling base provides the static `/ts-reviewer` compatibility link. Both the running Forgejo image and the replacement work with the existing entrypoint, which sources `/secrets/ts-reviewer.env` before execution. This avoids changing the running image's executable path before the replacement is viable.

Remove legacy reviewer concurrency tuning from the HomeLab declaration in the reviewed config migration. Runtime settings stay fixed in code. Retain the old Forgejo/proxy credentials and existing secret schema while the old image still needs them; remove them after the replacement has passed health and rollback readiness checks.

Materialize the GitHub App PEM to **`/var/srv/appdata/ts-reviewer/github-app.pem`** through the approved secret-store mechanism and add a read-only Incus disk device mapping it to **`/secrets/github-app.pem`**. Set **`TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH=/secrets/github-app.pem`** in the runtime dotenv. Ensure the uid-1654 runtime can read the PEM through the existing shifted mount convention. The workflow never creates or copies this credential. The [prepared target declarations](openbao-ts-reviewer-targets.json) store `TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM` as a raw PEM file, excluding it from dotenv. They are reviewable source only; no installed manifest was changed. The existing `ts-reviewer-env` materialization schema still requires legacy keys and needs an approved HomeLab schema update for the GitHub/Anthropic keys; merely entering an Anthropic key does not complete materialization or App authorization.

Before replacing the original materialized dotenv, the approved HomeLab secret-delivery transition must preserve its original generation **on Alpha only** at **`/var/srv/appdata/ts-reviewer/rollback/ts-reviewer.env`**, root-owned `0600` in a root-only directory. This contains the running Forgejo image's credentials and requires approval as a new persistent secret snapshot. Never retrieve its values into this thread or CI. The first deployment refuses to proceed without it. If legacy Vault keys were removed but the original materialized file still exists, snapshot that generation before any rematerialization; if the old credential generation is already unavailable, stop and resolve rollback readiness first. The roll utility restores this original dotenv before rebuilding the original image on a first-migration failure. Subsequent rolls snapshot the current dotenv locally with root-only permissions and restore it before image rollback.

Alpha's installed .NET SDK is 10.0.301; the project uses .NET 11. CI publishes both the application and deployment utility as self-contained Linux x64 binaries. No SDK upgrade is required on Alpha.

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
2. If replacement health is uncertain, restore **`RollbackEnv`** over `/var/srv/appdata/ts-reviewer/ts-reviewer.env` locally with the original runtime `0644` permissions. Stop the instance if running and rebuild the exact **`RollbackTag`** from that state: `sudo incus rebuild harbor:homelab/ts-reviewer:<RollbackTag> ts-reviewer`, then `sudo incus start ts-reviewer`. Verify `/healthz` before changing `:latest` or retrying. Production recovery and credential restoration need operator approval; do not print either file.
3. After a healthy recovery is confirmed, archive the deployment state and failed job's staged directory. Rerun the deployment job for the current `main` commit; the workflow rechecks freshness and every preflight gate. Preserve the archived state as rollback evidence.

Temporary delivery files live at `/tmp/ts-reviewer-<commit>-<run>-<attempt>`. They contain application artifacts, never production credentials. Rollback images and recovery state are retained for operator review; automatic registry pruning is deferred until retention is agreed.

Verified: Blacksmith execution, App installation and Actions permission, Anthropic authentication/model availability via a read-only request, local SDK image revision/digest preservation, and deployment rollback fixtures. Not verified: hosted-runner Alpha networking/SSH, protected environment creation, App PEM/token validity, production configuration changes, paid inference, or live deployment.
