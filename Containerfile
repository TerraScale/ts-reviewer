FROM gcr.io/distroless/cc-debian12:nonroot@sha256:777e96cf322c46bc32aca926c263624c4dc8d7cf37e2fa65ba2c7e697318ebbb
COPY artifacts/ci/container-publish/TerraScale.Reviewer /ts-reviewer
COPY artifacts/ci/container-publish/TerraScale.Reviewer.staticwebassets.endpoints.json /TerraScale.Reviewer.staticwebassets.endpoints.json
WORKDIR /tmp
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
USER 1654
ENTRYPOINT ["/ts-reviewer"]
