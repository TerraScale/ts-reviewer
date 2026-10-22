# Reviewer runtime status: 2026-10-22

This dated inventory supersedes the Alpha activation assumptions in
`deployment.md` and `alpha-ci-auth.md`. Alpha and its runners were deleted by
user decision Q-ALPHA-DELETE on 2026-10-14. Do not recreate that deployment.

## Evidence

- [Main CI run 38935985059](https://github.com/TerraScale/ts-reviewer/actions/runs/38935985059)
  passed tests, dependency auditing, application/container publication, and the
  environment check. It failed during OpenBao SSH signing, before Alpha SSH.
- `flyctl apps list -o terrascale` returned `No apps found`.
- `mariogk-dev` has no reviewer user service or matching service declaration,
  no Docker/Incus runtime, and no running Podman containers.
- Local inspection on `beta` found neither `ts-reviewer.service` nor
  `reviewer.service`, no reviewer system service, and no rootless or rootful
  Podman containers. Docker is not installed. SSH to beta was denied; local
  inspection was possible because this worktree runs there.
- The public `/healthz` request failed TLS negotiation (HTTP 000), so it did not
  establish a healthy service.
- GitHub API inspection of 97 PRs updated since 2026-10-19 (92 in
  `TerraScale/ts-orchestrator`, five in `TerraScale/terrascale`) found no submitted
  reviews, inline comments, or issue comments from
  `terrascale-ts-reviewer[bot]`, and no reviewer status on their current heads.
  The 20 issue comments found were from `MarioGK`.

No current runtime was located in these checks. This is not proof that no
uninspected host runs it.

## CI and hosting decision

CI uses GitHub-hosted runners, runs the existing offline checks, and publishes
the exact checked application with the .NET SDK `PublishContainer` target on
trusted main pushes. The deleted Alpha deployment, OIDC/SSH integration,
environment gate, deployment artifacts, and runner switch are removed from the
workflow. Image publication is not evidence of a running reviewer.

Q-REVIEWER-HOST was sent to the parent: prefer the existing `mariogk-dev` host;
the existing beta host is the alternative. Neither needs new paid capacity,
but this repository has no deployment integration for either host.

Before activation, replace the obsolete Anthropic-only provider and workspace
tiers with **GLM 5.3, then GLM 5.3 Flash only**, and establish automatic secret
delivery from the approved store. Do not deploy the current Anthropic policy,
copy secret values, or restore Alpha. A real review on a PR opened after the
replacement deployment remains the acceptance proof for runtime activation.
