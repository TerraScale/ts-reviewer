.RECIPEPREFIX := >
BINARY := bin/ts-reviewer

.PHONY: check test build image
check: test build

test:
>go test ./...

build:
>mkdir -p bin
>go build -trimpath -o $(BINARY) ./cmd/ts-reviewer

image:
>podman build --pull=missing -t git.terrascale.tech/terrascale/ts-reviewer:pr-$(shell git rev-parse --short HEAD) -f Containerfile .
