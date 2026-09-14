FROM gcr.io/distroless/cc-debian13:nonroot@sha256:54df941ed0d06a1bd95ef5e0ce391fd8d9f94b64782dc9a60062727849ee3f97
COPY artifacts/ci/container-publish/TerraScale.Reviewer /ts-reviewer
COPY artifacts/ci/container-publish/TerraScale.Reviewer.staticwebassets.endpoints.json /TerraScale.Reviewer.staticwebassets.endpoints.json
COPY artifacts/ci/container-publish/LocalEmbeddingsModel /LocalEmbeddingsModel
WORKDIR /tmp
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
USER 1654
ENTRYPOINT ["/ts-reviewer"]
