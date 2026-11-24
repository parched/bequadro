# Agent Instructions

Bequadro is a FlatBuffers source generator and runtime for C#, and a command-line tool that generates TypeScript and Python from the same schemas. It has its own solution, `Bequadro.sln`, and nothing in it may depend on anything outside this repository.

## Documents

Read these before changing anything:

- [REQUIREMENTS.md](REQUIREMENTS.md) and [requirements/](requirements/): the specification. It is the source of truth for behavior.
- [ARCHITECTURE.md](ARCHITECTURE.md): how the code is organized and why.
- [CONVENTIONS.md](CONVENTIONS.md): coding conventions.
- [README.md](README.md): the documentation for users of the package.

**These documents must stay up to date.** Any change that adds, removes or alters behavior must update the matching requirement, with its acceptance criteria and design decisions, in the same change. Any change to structure or implementation approach must update ARCHITECTURE.md. Any change visible to users (API, setup, diagnostics) must update README.md. If code and requirements disagree, treat it as a bug in one of them: work out which, and fix it. Don't let them drift.

## Building and Testing

- `.devcontainer/` provides the pinned .NET SDK (from `global.json`), `uv` (which also provides Python, pyright and mypy), Node for the TypeScript tests, and `clang` and `zlib1g-dev` for Native AOT. State that should survive rebuilds goes in the `/persist` volume (`$DEVCONTAINER_PERSISTENT_DIR`), shared by all worktrees. Claude Code's config and bash history are kept there, but Claude Code isn't installed.
- `uv run build.py --check` runs the tests of `build.py` itself (`tests/build`), checks formatting, builds with warnings as errors, runs all tests, and tests the package by building and running `tests/PackageConsumer` against it, leaving the packages it tested in `artifacts/` as `--pack` does. It then publishes the CLI with Native AOT, packs and tests the npm packages and the Vite plugin (`tests/npm`), checks that it generates the source generator's schema fingerprint in both targets, and runs the TypeScript checks in `tests/typescript` with it (`tsc` with TypeScript 7 and 5.8, and `node --test`), and the Python checks in `tests/python` (pytest on Python 3.12 and 3.15, pyright and mypy). Those use fixtures and random schemas that the C# tests write to `artifacts/`, so run the C# tests first when running them by hand.
- `uv run build.py --fix` applies formatting.
- `uv run build.py --pack` produces the NuGet package and its symbol package in `artifacts/package/` (version `0.0.0-unreleased`, or the one a release tag names with `--release-tag v<version>`), the CLI in `artifacts/cli/<rid>/`, and the npm packages for this platform in `artifacts/npm/`. `uv run build.py --check-npm` packs the npm packages and runs their tests in `tests/npm` (`--check` does this too); CI runs it on each platform to build and test that platform's package.
- When a snapshot test fails it writes a `*.received.cs` (or `.ts`, `.py`) next to the `*.verified.cs` (or `.ts`, `.py`). After reviewing the diff, rename it over the verified file, or rerun the tests with `BEQUADRO_ACCEPT_SNAPSHOTS=1` to accept all snapshots.
- After changing the generator, run `dotnet build-server shutdown` before building a project that consumes it directly. The compiler server caches analyzer assemblies by path, so it may keep running the old generator.

## Releasing

Publish a GitHub release with a new tag `v<version>`, such as `v1.2.0` or `v1.2.0-rc.1`. The tag is the only source of the version: nothing in the repository changes, and every other build is `0.0.0-unreleased`. CI checks the tag's format, builds every package with its version (`build.py --release-tag`), attaches the six standalone CLI binaries to the GitHub release (CLI-001), and publishes the NuGet package to nuget.org and the npm packages to npm (CON-007, CLI-009). If a publishing or upload job fails part way, rerun the failed jobs. They skip packages already published and binaries already attached.

Both registries use trusted publishing (OIDC), set up once by hand:

- **nuget.org:** a trusted publishing policy for this repository, workflow `build.yml` and environment `nuget`, and the policy's owner as the `NUGET_USER` variable of the `nuget` environment.
- **npm:** trusted publishing can only be set up for a package that exists, so the first release needs a token:
  1. Create the `@bequadro` organization on npm.
  2. Create a granular access token with read and write access to `bequadro` and the `@bequadro` scope, expiring in a day, and store it as the `NPM_TOKEN` secret of the `npm` environment.
  3. Publish the first release.
  4. For each of the seven packages (`bequadro` and `@bequadro/<platform>` for each platform in CLI-009), add a trusted publisher on npmjs.com: GitHub Actions, repository `parched/bequadro`, workflow `build.yml`, environment `npm`. Then set its publishing access to require two-factor authentication and disallow tokens.
  5. Delete the `NPM_TOKEN` secret and revoke the token. Later releases publish with the job's OIDC token, with provenance.

  A platform package added later must be published once with a token the same way before it can have a trusted publisher.

## Working Rules

- Each requirement needs tests that demonstrate its acceptance criteria. New diagnostics need tests that assert the diagnostic ID and location.
- Never weaken a test or a requirement to make a change pass without saying so explicitly in the change description.
