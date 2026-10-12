# Debian trixie rather than distroless: the OpenCodeReview engine needs a
# shell-less static binary plus git (>= 2.41, trixie ships 2.47) on PATH.
# The .NET SDK adds the tested self-contained app with PublishContainer.
# Base is digest-pinned like the distroless base before it — bump the digest
# with each roll (security updates included in trixie-slim re-publishes).
FROM docker.io/library/debian:trixie-slim@sha256:7792b1f7702a86946cd518db72b6a407302c3e9bc1635634368b878189e8221c

# OpenCodeReview CLI (github.com/alibaba/open-code-review): the npm package is
# just a launcher around this static Go binary, so fetch the platform package
# directly (sha256-gated) and skip shipping Node.js entirely.
ARG OCR_VERSION=1.12.12
ARG OCR_SHA256=6d3f6004bb062e418bac4ece1c72e9ecec2b15ec06ad7bfb7ad7a7c8ac83d52e
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git \
 && curl -fsSL "https://registry.npmjs.org/@alibaba-group/ocr-linux-x64/-/ocr-linux-x64-${OCR_VERSION}.tgz" -o /tmp/ocr.tgz \
 && echo "${OCR_SHA256}  /tmp/ocr.tgz" | sha256sum -c - \
 && tar -xzf /tmp/ocr.tgz -C /tmp \
 && install -m 0755 /tmp/package/bin/opencodereview /usr/local/bin/ocr \
 && ocr version \
 && ln -s /app/TerraScale.Reviewer /ts-reviewer \
 && rm -rf /var/lib/apt/lists/* /tmp/package /tmp/ocr.tgz

WORKDIR /tmp
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp HOME=/tmp
USER 1654
