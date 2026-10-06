# syntax=docker/dockerfile:1.7
FROM rust:1.92.0-bookworm AS builder
WORKDIR /workspace

COPY rust-toolchain.toml ./rust-toolchain.toml
COPY server/Cargo.toml ./server/Cargo.toml
COPY server/protocol/Cargo.toml ./server/protocol/Cargo.toml

RUN mkdir -p server/src server/protocol/src \
    && printf 'fn main() {}\n' > server/src/main.rs \
    && printf 'pub fn docker_lockfile_stub() {}\n' > server/src/lib.rs \
    && printf 'pub fn docker_lockfile_stub() {}\n' > server/protocol/src/lib.rs \
    && cargo generate-lockfile --manifest-path server/Cargo.toml

COPY server/protocol ./server/protocol
COPY server/src ./server/src

RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/usr/local/cargo/git \
    --mount=type=cache,target=/workspace/server/target \
    cargo build --release --manifest-path server/Cargo.toml --package valhalla_server --bin valhalla-relay \
    && test -s server/target/release/valhalla-relay \
    && mkdir -p /out \
    && cp server/target/release/valhalla-relay /out/valhalla-relay \
    && test -s /out/valhalla-relay

FROM scratch AS artifacts
COPY --from=builder /out/valhalla-relay /valhalla-relay
