# Debian trixie rather than distroless: the OpenCodeReview engine needs a
# shell-less static binary plus git (>= 2.41, trixie ships 2.47) on PATH.
# The .NET SDK adds the tested self-contained app with PublishContainer.
# Base is digest-pinned like the distroless base before it — bump the digest
# with each roll (security updates included in trixie-slim re-publishes).
FROM docker.io/library/debian:trixie-slim@sha256:a66b19aa552f595b4ab276c6b4f9b24fa4546632c5ecd88cccdced4a6866323a

# OpenCodeReview CLI (github.com/alibaba/open-code-review): the npm package is
# just a launcher around this static Go binary, so fetch the platform package
# directly (sha256-gated) and skip shipping Node.js entirely.
ARG OCR_VERSION=1.12.12
ARG OCR_SHA256=d87e7f308441bced6b134b03468d0e600485d832b649f9e06727b1d68b3a1df0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git \
 && curl -fsSL "https://registry.npmjs.org/@alibaba-group/ocr-linux-arm64/-/ocr-linux-arm64-${OCR_VERSION}.tgz" -o /tmp/ocr.tgz \
 && echo "${OCR_SHA256}  /tmp/ocr.tgz" | sha256sum -c - \
 && tar -xzf /tmp/ocr.tgz -C /tmp \
 && install -m 0755 /tmp/package/bin/opencodereview /usr/local/bin/ocr \
 && ocr version \
 && install -d -m 0700 -o 65532 -g 65532 /data \
 && rm -rf /var/lib/apt/lists/* /tmp/package /tmp/ocr.tgz

WORKDIR /tmp
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp HOME=/tmp
USER 65532:65532
