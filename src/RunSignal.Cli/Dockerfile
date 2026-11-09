# The runsignal CLI as a native executable, for images without .NET. Copy the one that matches your image:
#
#   COPY --from=ghcr.io/gokhanertogan/runsignal:latest /runsignal /usr/local/bin/runsignal        # glibc
#   COPY --from=ghcr.io/gokhanertogan/runsignal:latest /runsignal-musl /usr/local/bin/runsignal   # Alpine
#
# /runsignal links against glibc 2.28 or later: Debian 10+, Ubuntu 20.04+, RHEL, Rocky and AlmaLinux 8+, Amazon
# Linux 2023, and the Debian-based images of most languages. /runsignal-musl is for Alpine.
# Exporting over HTTPS needs OpenSSL (libssl) and CA certificates in the image; without libssl, runsignal exec
# still runs the command and returns its exit code, but does not export.
#
# Build from the repository root: docker build -f src/RunSignal.Cli/Dockerfile .

# A native executable needs the glibc of the machine it is built on, or newer. AlmaLinux 8 (glibc 2.28, supported
# until 2029) keeps it runnable on older distributions than the .NET SDK images (glibc 2.38+) would. The SDK comes
# from the official install script on the 10.0 channel, so the self-contained binary embeds the latest patched
# runtime.
FROM almalinux:8 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
RUN dnf install -y --setopt=install_weak_deps=False clang zlib-devel libicu findutils tar gzip \
 && dnf clean all \
 && curl -fsSL --retry 3 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
 && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
WORKDIR /src
COPY Directory.Build.props ./
COPY docs/dotnet.md docs/icon.png ./docs/
COPY src/ ./src/
RUN dotnet publish src/RunSignal.Cli -c Release -p:RunSignalNative=true -o /out \
 && /out/runsignal --help > /dev/null

FROM alpine:3.22 AS build-musl
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
RUN apk add --no-cache bash curl clang build-base zlib-dev icu-libs libgcc libstdc++ \
 && curl -fsSL --retry 3 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
 && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
WORKDIR /src
COPY Directory.Build.props ./
COPY docs/dotnet.md docs/icon.png ./docs/
COPY src/ ./src/
RUN dotnet publish src/RunSignal.Cli -c Release -p:RunSignalNative=true -o /out \
 && /out/runsignal --help > /dev/null

# runtime-deps has only what the binary needs (glibc and certificates), so `docker run` works too.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY --from=build /out/runsignal /runsignal
COPY --from=build-musl /out/runsignal /runsignal-musl
ENTRYPOINT ["/runsignal"]
