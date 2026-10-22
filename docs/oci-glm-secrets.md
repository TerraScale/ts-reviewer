# Reviewer OCI Vault secret contract

Run the private setup script on `mariogk-dev`:

```sh
bash ~/.cache/ts-vault-build/store-ts-reviewer-20261022.sh
```

The reviewed source is [`scripts/store-reviewer-secrets.sh`](../scripts/store-reviewer-secrets.sh).
It requires existing `HOMELAB_BROWSER` OCI operator access. It reads the GitHub
App RSA PEM and GLM key with terminal echo disabled, stores both through stdin,
and generates three distinct 256-bit first-party tokens. Reruns preserve generated
tokens; explicit vendor updates use ETags. Only names/outcomes print. Partial
failure can leave earlier writes complete; no automatic retry or permission changes.

The [workload handoff](oci-workload-handoff.md) supplies the Vault and placement
contract. Add this entry to its `secrets` map when the GLM provider layer merges:

```json
"TS_REVIEWER_GLM_API_KEY_FILE": {
  "vault": "ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq",
  "name": "gru-1-ts-reviewer-glm-api-key"
}
```

The other names are `gru-1-ts-reviewer-github-private-key`,
`gru-1-ts-reviewer-webhook-secret`, `gru-1-ts-reviewer-context-api-token`, and
`gru-1-ts-reviewer-operator-token`. The PEM uses `TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH`;
webhook/context/operator use their existing `_FILE` bindings. Never put plain
credential values in the workload environment. Ambiguous plain/file sources and
missing, unreadable or blank files fail startup. Terminal CR/LF is removed; other
content is preserved.

Models are fixed to `glm-5.3`, then `glm-5.3-flash`, at Z.AI's
`https://api.z.ai/api/anthropic` endpoint. OCR requires its Messages wire protocol;
this vendor-owned endpoint/header naming is unavoidable. Bearer auth uses only
the GLM key. There are three attempts per call: primary twice, Flash once, then
failure without a verdict. No model, retry or provider configuration switches.

Before activation, merge the GLM provider layer, publish a successful native
ARM64 main image and let the orchestrator lane verify placement/ingress. Health
alone is insufficient: link the bot review on a PR opened after deployment.
