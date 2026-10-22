# OCI workload HANDOFF: ts-reviewer

Orchestrator lane owns deployment on **ts-gru-1** through ts-orch. One replica;
GHCR image; OCI Vault through instance principal. Candidate only: activation
requires the GLM provider and placement work below; use the [Vault contract](oci-glm-secrets.md).

This entry uses the current `ServiceSpec` schema from ts-orchestrator
`bf0e074ba53e1ed64b3e8abdff795acb6b013145` (`internal/swarmruntime/service.go`),
with the regional Vault reference from `deploy/m1/services/ts-auth.json`.
App identities come from the reviewer's existing provisioning constants.
The Vault OCID and GitHub numeric IDs are unavoidable vendor-issued identities.
Port 8092 was free in read-only `ss -lnt` inspection on the target.

```json
{
  "name": "ts-reviewer",
  "image": "ghcr.io/terrascale/ts-reviewer@sha256:<verified-arm64-digest>",
  "env": {
    "TS_REVIEWER_LISTEN": "http://+:8092",
    "TS_REVIEWER_CONTEXT_DATABASE": "/data/reviewer-context.db",
    "TS_REVIEWER_GITHUB_APP_ID": "5279339",
    "TS_REVIEWER_GITHUB_INSTALLATION_ID": "170416009",
    "TS_REVIEWER_BOT_LOGIN": "terrascale-ts-reviewer[bot]",
    "TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH": "/run/ts-orch/secrets/ts_reviewer_github_private_key_file"
  },
  "secrets": {
    "TS_REVIEWER_GITHUB_PRIVATE_KEY_FILE": {
      "vault": "ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq",
      "name": "gru-1-ts-reviewer-github-private-key"
    },
    "TS_REVIEWER_WEBHOOK_SECRET_FILE": {
      "vault": "ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq",
      "name": "gru-1-ts-reviewer-webhook-secret"
    },
    "TS_REVIEWER_CONTEXT_API_TOKEN_FILE": {
      "vault": "ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq",
      "name": "gru-1-ts-reviewer-context-api-token"
    },
    "TS_REVIEWER_OPERATOR_TOKEN_FILE": {
      "vault": "ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq",
      "name": "gru-1-ts-reviewer-operator-token"
    }
  },
  "mounts": [{"source": "/var/lib/ts-data/ts-reviewer", "target": "/data"}],
  "port": 8092,
  "readyPath": "/healthz",
  "hostnames": ["ts-reviewer.mariogk.com"],
  "replicas": 1,
  "regions": ["gru"]
}
```

1. **Image — reviewer lane.** Target reports `aarch64`, hostname `gru-1-ctl-1`.
   CI now builds/tests natively on `ubuntu-24.04-arm`, publishes `linux-arm64`,
   and verifies pinned ARM64 OCR. The image matches ts-orch's UID:GID
   `65532:65532` and creates its `/data` mountpoint. Replace the digest placeholder
   with the successful main image's actual ARM64 digest; never apply this template.
2. **Placement/ingress — orch lane.** `regions` selects a region, not a node;
   `ServiceSpec` has no host selector. Supply validated ts-gru-1-only placement.
   Confirm persistent storage, writable scratch and the existing webhook hostname's
   DNS/TLS route to port 8092. The App endpoint is `POST /webhook`.
3. **Secrets — both lanes.** Proposed names are not proof the secrets exist.
   `ReadInstanceSecrets` discovers the cell/Vault and validates service-owned refs
   with instance principal. ts-orch delivers read-only tmpfs files and sets `_FILE`
   environment values to paths. The [Vault contract](oci-glm-secrets.md) specifies
   the reviewer file bindings and private user-run setup command. Generate
   first-party credentials automatically and deliver the existing GitHub PEM and
   vendor key through Vault; never copy values to CI, arguments or Git.
4. **Provider — reviewer lane.** Activate only after the GLM provider layer
   and its file binding merge. Models are **GLM 5.3, then GLM 5.3 Flash only**.
5. **Proof.** Health HTTP 200 alone is insufficient. After deployment, verify
   automatic recovery and link the bot's actual review on a newly opened PR.

No orchestration repository, infrastructure, secret values or live service was
changed while preparing this entry. No paid model call was made.
