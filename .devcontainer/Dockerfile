FROM mcr.microsoft.com/devcontainers/base:ubuntu-24.04

COPY --from=ghcr.io/astral-sh/uv:0.13.8 /uv /uvx /usr/local/bin/

# Node runs the TypeScript tests and type checks generated TypeScript. Copied from the official image, like uv,
# so the container gets the right architecture without a package repository.
COPY --from=node:26.13.1-bookworm-slim /usr/local/bin/node /usr/local/bin/
COPY --from=node:26.13.1-bookworm-slim /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/npm
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

# Install the exact SDK that global.json pins, so the container never drifts from it.
# clang and zlib1g-dev are what Native AOT needs to link the CLI.
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
ENV PATH=$PATH:$DOTNET_ROOT
COPY global.json /tmp/global.json
RUN apt-get update \
    && apt-get install -y --no-install-recommends libicu74 clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --jsonfile /tmp/global.json --install-dir $DOTNET_ROOT \
    && rm /tmp/dotnet-install.sh /tmp/global.json
