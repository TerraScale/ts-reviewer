FROM golang:1.27-bookworm AS build
WORKDIR /src
COPY go.mod ./
COPY . .
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags='-s -w' -o /out/ts-reviewer ./cmd/ts-reviewer

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/ts-reviewer /ts-reviewer
USER nonroot:nonroot
EXPOSE 8080
ENTRYPOINT ["/ts-reviewer"]
