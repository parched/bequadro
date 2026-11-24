#!/usr/bin/env -S uv run --script
#
# /// script
# requires-python = "==3.13.*"
# dependencies = []
# ///

import argparse
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path
from subprocess import Popen

_IS_GITHUB_ACTIONS = os.getenv("GITHUB_ACTIONS") == "true"

# npm is a .cmd script on Windows, which Popen only finds by its full path
NPM = shutil.which("npm") or "npm"


def run(*command: str | Path, cwd: Path | None = None):
    msg = f"Running {' '.join(str(c) for c in command)}"
    if _IS_GITHUB_ACTIONS:
        print(f"::group::{msg}", flush=True)
    else:
        print(msg, flush=True)

    process = Popen(command, cwd=cwd)
    try:
        process.wait()
    except KeyboardInterrupt:
        process.terminate()
        process.wait()
    finally:
        if _IS_GITHUB_ACTIONS:
            print("::endgroup::", flush=True)

    if process.returncode != 0:
        raise Exception(f"{command} failed with exit code {process.returncode}")


def pack(output: Path, *options: str) -> Path:
    run("dotnet", "pack", "src/Bequadro/Bequadro.csproj", "--configuration=Release", "--output", output, *options)
    return next(output.glob("Bequadro.*.nupkg"))


REPOSITORY = "https://github.com/parched/bequadro"


def check_package_contents(package: Path) -> None:
    """CON-001: the runtime, the generator and its props files, nothing else in analyzers/, no dependencies, and the MIT
    license. CON-007: the repository and the commit, and a symbol package whose symbols use Source Link to fetch that
    commit's sources."""
    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        nuspec = archive.read(next(n for n in names if n.endswith(".nuspec"))).decode()

    commit = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    symbols = package.with_suffix(".snupkg")
    pdb = "lib/net11.0/Bequadro.pdb"
    symbol_problems = []
    if not symbols.exists():
        symbol_problems.append("there is no symbol package")
    else:
        with zipfile.ZipFile(symbols) as archive:
            if pdb not in archive.namelist():
                symbol_problems.append(f"the symbol package has no {pdb}")
            # A portable PDB keeps the Source Link JSON as it is, in its blob heap
            elif f'"{REPOSITORY.replace("github.com", "raw.githubusercontent.com")}/{commit}/*"'.encode() not in archive.read(pdb):
                symbol_problems.append(f"the symbols don't use Source Link for commit {commit}")

    runtime = "lib/net11.0/Bequadro.dll"
    generator = "analyzers/dotnet/cs/Bequadro.Generator.dll"
    props = ["build/Bequadro.props", "buildTransitive/Bequadro.props"]
    problems = [f"{name} is missing" for name in (runtime, generator, *props) if name not in names]
    problems += [f"{name} is unexpected" for name in sorted(names) if name.startswith("analyzers/") and name != generator]
    if "<dependency " in nuspec:
        problems.append("the package has dependencies")
    if '<license type="expression">MIT</license>' not in nuspec:
        problems.append("the package's license isn't the MIT expression")
    if not re.search(rf'<repository type="git" url="{re.escape(REPOSITORY)}(\.git)?"[^>]* commit="{commit}"', nuspec):
        problems.append(f"the package doesn't name its repository and commit {commit}")
    problems += symbol_problems
    if problems:
        raise Exception(f"{package.name}: {'; '.join(problems)}")


def pack_package(root: Path, *options: str) -> Path:
    """CON-007: pack the package and its symbol package into artifacts/package/, and check them."""
    output = root / "artifacts" / "package"
    shutil.rmtree(output, ignore_errors=True)
    package = pack(output, *options)
    check_package_contents(package)
    return package


def check_package(root: Path, package: Path) -> None:
    """CON-001: build and run a consumer project against the package, from the folder it is in."""
    feed = package.parent
    with tempfile.TemporaryDirectory() as temp:
        # A copy outside the repository, so none of its build files apply
        consumer = Path(temp) / "consumer"
        shutil.copytree(root / "tests" / "PackageConsumer", consumer, ignore=shutil.ignore_patterns("bin", "obj", "packages"))
        shutil.copy(root / "global.json", consumer)
        (consumer / "nuget.config").write_text(
            "<configuration><packageSources><clear />"
            f'<add key="local" value="{feed}" />'
            "</packageSources></configuration>"
        )
        version = package.name.removeprefix("Bequadro.").removesuffix(".nupkg")
        run("dotnet", "run", "--project", consumer / "Consumer.csproj", f"-p:BequadroVersion={version}")


def runtime_identifier() -> str:
    system = {"Linux": "linux", "Darwin": "osx", "Windows": "win"}[platform.system()]
    architecture = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}[platform.machine().lower()]
    return f"{system}-{architecture}"


def publish_cli(root: Path) -> Path:
    """CLI-001: publish the CLI with Native AOT for this platform to artifacts/cli/<rid>/, and return the executable."""
    rid = runtime_identifier()
    output = root / "artifacts" / "cli" / rid
    # TreatWarningsAsErrors also makes the AOT compiler fail on its trimming and AOT warnings (IlcTreatWarningsAsErrors)
    run("dotnet", "publish", "src/Bequadro.Cli/Bequadro.Cli.csproj", "--configuration=Release", "--runtime", rid, "--output", output, "-p:TreatWarningsAsErrors=true")
    return output / ("bequadro.exe" if rid.startswith("win") else "bequadro")


def generate_random(cli: Path, root: Path, target: str, output: Path, prefix: str = "") -> int:
    """QUA-003: generate code for every random schema the generator tests wrote, each into its own directory named
    prefix + seed. Returns how many had no errors, and fails if there were no schemas, or none generated code, so the
    checks that use the code can't pass without testing anything."""
    shutil.rmtree(output, ignore_errors=True)
    schemas = sorted((root / "artifacts" / "random-schemas").glob("*.fbs"))
    if not schemas:
        raise Exception("No random schemas in artifacts/random-schemas: run the C# tests first")
    generated = 0
    for schema in schemas:
        options = ["--require-scalars-without-default"] if schema.name.endswith(".required.fbs") else []
        directory = output / (prefix + schema.name.split(".")[0])
        command = [cli, "generate", "--target", target, "--out", directory, *options, schema]
        result = subprocess.run(command, capture_output=True, text=True)
        # Exit code 1 is a schema with errors, for which nothing is generated
        if result.returncode not in (0, 1):
            raise Exception(f"{schema.name}: exit code {result.returncode}\n{result.stdout}{result.stderr}")
        generated += result.returncode == 0

    if generated == 0:
        raise Exception(f"None of the {len(schemas)} random schemas generated {target} code")
    return generated


# SER-015: how each target's fingerprint module spells the fingerprint
FINGERPRINT_MODULES = {
    "typescript": ("bequadro_schema.ts", re.compile(r'export const fingerprint: string = "([0-9a-f]{32})";')),
    "python": ("bequadro_schema.py", re.compile(r'FINGERPRINT: Final\[str\] = "([0-9a-f]{32})"')),
}


def check_fingerprint(root: Path, cli: Path) -> None:
    """SER-015: the AOT executable gives the runtime tests' schemas the fingerprint that the source generator gave them,
    which CrossLanguageFixtureTests writes, in every target."""
    expected = (root / "artifacts" / "cross-language" / "fingerprint.txt").read_text(encoding="utf-8")
    schemas = root / "tests" / "Bequadro.Tests" / "Schemas"
    with tempfile.TemporaryDirectory() as temp:
        for target, (module, pattern) in FINGERPRINT_MODULES.items():
            output = Path(temp) / target
            run(cli, "generate", "--target", target, "--out", output, schemas)
            match = pattern.search((output / module).read_text(encoding="utf-8"))
            actual = match.group(1) if match else None
            if actual != expected:
                raise Exception(f"The {target} fingerprint is {actual}, but the C# one is {expected}")
    print(f"Every target has the fingerprint {expected}", flush=True)


def check_typescript(root: Path, cli: Path) -> None:
    """QUA-003: type-check generated TypeScript and run its tests, with code generated by the AOT executable."""
    tests = root / "tests" / "typescript"
    # The runtime tests' schemas, and the TypeScript tests' own (names, nesting and import cycles)
    schemas = [root / "tests" / "Bequadro.Tests" / "Schemas", tests / "schemas"]
    run(NPM, "ci", "--no-fund", "--no-audit", cwd=tests)
    run(cli, "generate", "--target", "typescript", "--out", tests / "generated", *schemas)
    run(cli, "generate", "--target", "typescript", "--import-extension", "js", "--out", tests / "generated-js", *schemas)
    count = generate_random(cli, root, "typescript", tests / "generated-random")
    print(f"Generated TypeScript for {count} random schemas without errors", flush=True)
    for tsc in (tests / "node_modules" / ".bin" / "tsc", tests / "node_modules" / "typescript-5.8" / "bin" / "tsc"):
        for config in ("tsconfig.json", "tsconfig.js.json", "tsconfig.random.json"):
            run(tsc, "-p", config, cwd=tests)
    run("node", "--test", "test/", cwd=tests)


# CLI-009: the npm platform package for each runtime identifier, named @bequadro/<process.platform>-<process.arch>
NPM_PLATFORMS = {
    "osx-arm64": "darwin-arm64",
    "osx-x64": "darwin-x64",
    "linux-arm64": "linux-arm64",
    "linux-x64": "linux-x64",
    "win-arm64": "win32-arm64",
    "win-x64": "win32-x64",
}


# CON-007: a release tag is v<major>.<minor>.<patch>, optionally followed by -<prerelease>, a valid version in semantic
# versioning 2.0.0, without build metadata. Numeric identifiers have no leading zeros, and prerelease identifiers are
# numeric or contain a letter or hyphen, which npm's semver and NuGet both require.
_NUMERIC = r"(?:0|[1-9][0-9]*)"
_PRERELEASE_IDENTIFIER = rf"(?:{_NUMERIC}|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
RELEASE_TAG = re.compile(
    rf"v(?P<version>(?P<major>{_NUMERIC})\.(?P<minor>{_NUMERIC})\.(?P<patch>{_NUMERIC})"
    rf"(?:-{_PRERELEASE_IDENTIFIER}(?:\.{_PRERELEASE_IDENTIFIER})*)?)"
)

# Every build that isn't a release has this version, so no release may (CON-007)
UNRELEASED_VERSION = "0.0.0-unreleased"

# NuGet's version numbers are 32-bit integers, and nuget.org takes versions of up to 64 characters. npm allows more.
_MAXIMUM_NUMBER = 2**31 - 1
_MAXIMUM_LENGTH = 64


def release_version(tag: str) -> str:
    """CON-007: the version a release tag names, which every package of the release gets, so it is what both npm and
    NuGet accept. The tag is the only source of a release's version."""
    match = RELEASE_TAG.fullmatch(tag)
    if match is None:
        raise Exception(f"The release tag {tag!r} isn't v<major>.<minor>.<patch> or v<major>.<minor>.<patch>-<prerelease>")
    version = match.group("version")
    if any(int(match.group(part)) > _MAXIMUM_NUMBER for part in ("major", "minor", "patch")):
        raise Exception(f"The release tag {tag!r} has a version number above {_MAXIMUM_NUMBER}, which NuGet doesn't allow")
    if len(version) > _MAXIMUM_LENGTH:
        raise Exception(f"The release tag {tag!r} has a version longer than {_MAXIMUM_LENGTH} characters, which nuget.org doesn't allow")
    if version == UNRELEASED_VERSION:
        raise Exception(f"The release tag {tag!r} names the version of builds that aren't releases")
    return version


def version(root: Path) -> str:
    """The version being built: BEQUADRO_VERSION, which Directory.Build.props also reads, or its default."""
    if override := os.getenv("BEQUADRO_VERSION"):
        return override
    match = re.search(r"<Version>([^<]+)</Version>", (root / "Directory.Build.props").read_text())
    if match is None:
        raise Exception("Directory.Build.props has no <Version>")
    return match.group(1)


def npm_pack(directory: Path, destination: Path) -> Path:
    destination.mkdir(parents=True, exist_ok=True)
    command = [NPM, "pack", "--json", "--pack-destination", str(destination)]
    result = subprocess.run(command, cwd=directory, capture_output=True, text=True)
    if result.returncode != 0:
        raise Exception(f"npm pack failed in {directory}:\n{result.stderr}")
    return destination / json.loads(result.stdout)[0]["filename"]


# CLI-009: the oldest glibc the Linux executables support, as documented
MINIMUM_GLIBC = (2, 34)


def check_glibc(cli: Path) -> None:
    """CLI-009: check that a Linux executable needs no newer glibc than MINIMUM_GLIBC. The versions it needs are the
    GLIBC_x.y symbol versions in its dynamic string table."""
    needed = {tuple(int(part) for part in match.split(b".")) for match in re.findall(rb"GLIBC_(\d+\.\d+(?:\.\d+)?)\x00", cli.read_bytes())}
    newest = max(needed, default=(0, 0))
    if newest > MINIMUM_GLIBC:
        version = ".".join(str(part) for part in newest)
        raise Exception(f"{cli} needs glibc {version}, newer than the documented minimum, {MINIMUM_GLIBC[0]}.{MINIMUM_GLIBC[1]}")


def pack_npm(root: Path, cli: Path) -> list[Path]:
    """CLI-009: pack the platform package for this platform, holding the AOT executable cli, and the main package,
    into artifacts/npm/, both with the version of the NuGet package. Returns the tarballs."""
    rid = runtime_identifier()
    if rid not in NPM_PLATFORMS:
        raise Exception(f"There is no npm package for {rid}")
    package_version = version(root)
    if rid.startswith("linux"):
        check_glibc(cli)
    output = root / "artifacts" / "npm"
    shutil.rmtree(output, ignore_errors=True)
    source = root / "npm" / "bequadro"
    manifest = json.loads((source / "package.json").read_text())
    shared = {key: manifest[key] for key in ("license", "repository", "engines")}

    with tempfile.TemporaryDirectory() as temp:
        node_platform = NPM_PLATFORMS[rid]
        os_name, cpu = node_platform.split("-")
        name = f"@bequadro/{node_platform}"
        platform_package = Path(temp) / node_platform
        (platform_package / "bin").mkdir(parents=True)
        shutil.copy2(cli, platform_package / "bin" / cli.name)
        (platform_package / "bin" / cli.name).chmod(0o755)
        platform_manifest = {
            "name": name,
            "version": package_version,
            "description": f"The bequadro executable for {node_platform}, which the bequadro package installs.",
            **shared,
            "os": [os_name],
            "cpu": [cpu],
            # The Linux executables link against glibc, so npm skips them where the C library is musl
            **({"libc": ["glibc"]} if os_name == "linux" else {}),
            "files": ["bin"],
            "preferUnplugged": True,
        }
        (platform_package / "package.json").write_text(json.dumps(platform_manifest, indent=2) + "\n")
        shutil.copy(root / "LICENSE", platform_package)
        (platform_package / "README.md").write_text(
            f"# {name}\n\nThe native `bequadro` executable for {node_platform}. Install "
            "[bequadro](https://www.npmjs.com/package/bequadro) instead, which depends on this package.\n"
        )
        tarballs = [npm_pack(platform_package, output)]

        shutil.rmtree(source / "dist", ignore_errors=True)
        run(NPM, "ci", "--no-fund", "--no-audit", cwd=source)
        run(NPM, "run", "build", cwd=source)
        main = Path(temp) / "bequadro"
        shutil.copytree(source / "dist", main / "dist")
        shutil.copy(source / "README.md", main)
        shutil.copy(root / "LICENSE", main)
        manifest["version"] = package_version
        manifest["optionalDependencies"] = {f"@bequadro/{p}": package_version for p in sorted(NPM_PLATFORMS.values())}
        for key in ("scripts", "devDependencies"):
            manifest.pop(key, None)
        (main / "package.json").write_text(json.dumps(manifest, indent=2) + "\n")
        tarballs.append(npm_pack(main, output))

    return tarballs


def check_npm(root: Path, tarballs: list[Path]) -> None:
    """CLI-009, CLI-010: install the packed npm packages into tests/npm, type-check its tests against them, and run
    them."""
    tests = root / "tests" / "npm"
    run(NPM, "ci", "--no-fund", "--no-audit", cwd=tests)
    run(NPM, "install", "--no-save", "--no-fund", "--no-audit", *tarballs, cwd=tests)
    run(NPM, "exec", "--", "tsc", "-p", "tsconfig.json", cwd=tests)
    run("node", "--test", "test/", cwd=tests)


def stage_release_cli(packages: Path, output: Path, tag: str) -> None:
    """CLI-001: stage one standalone executable per platform from the npm tarballs CI tested."""
    package_version = release_version(tag)
    output.mkdir(parents=True, exist_ok=True)
    for rid, node_platform in NPM_PLATFORMS.items():
        package = packages / f"bequadro-{node_platform}-{package_version}.tgz"
        suffix = ".exe" if rid.startswith("win-") else ""
        with tarfile.open(package, "r:gz") as archive:
            with archive.extractfile("package/package.json") as manifest_file:
                manifest = json.load(manifest_file)
            if manifest["name"] != f"@bequadro/{node_platform}" or manifest["version"] != package_version:
                raise ValueError(f"{package.name} has the wrong package name or version")
            binary = archive.getmember(f"package/bin/bequadro{suffix}")
            if not binary.isfile() or not binary.size:
                raise ValueError(f"{package.name} has no regular executable at {binary.name}")
            asset = output / f"bequadro-{rid}{suffix}"
            with archive.extractfile(binary) as source, asset.open("wb") as destination:
                shutil.copyfileobj(source, destination)
            asset.chmod(0o755)


# The oldest Python generated code supports (PY-001), and the newest release
PYTHON_VERSIONS = ["3.12", "3.15"]


def check_python(root: Path, cli: Path) -> None:
    """QUA-003: type-check generated Python and run its tests, with code generated by the AOT executable."""
    tests = root / "tests" / "python"
    # The C# tests' schemas, and schemas only the Python tests use
    run(cli, "generate", "--target", "python", "--out", tests / "generated", root / "tests" / "Bequadro.Tests" / "Schemas", tests / "schemas")
    random = tests / "generated_random"
    count = generate_random(cli, root, "python", random, prefix="s")
    (random / "__init__.py").write_text("")
    print(f"Generated Python for {count} random schemas without errors", flush=True)
    for version in PYTHON_VERSIONS:
        run("uv", "run", "--python", version, "pytest", "-q", cwd=tests)
    run("uv", "run", "pyright", cwd=tests)
    run("uv", "run", "mypy", "generated", "generated_random", "test", cwd=tests)


def main() -> None:
    root = Path(__file__).parent
    os.chdir(root)

    parser = argparse.ArgumentParser(description="Build, check and pack Bequadro")
    parser.add_argument("--fix", action="store_true", help="Apply formatting fixes")
    parser.add_argument("--check", action="store_true", help="Check formatting, build with warnings as errors, run all tests, and test the package")
    parser.add_argument(
        "--pack",
        action="store_true",
        help="Create the NuGet package in artifacts/package, the CLI in artifacts/cli, and the npm packages for this platform in artifacts/npm",
    )
    parser.add_argument("--check-npm", action="store_true", help="Pack the npm packages for this platform and run their tests")
    parser.add_argument(
        "--stage-release-cli",
        type=Path,
        metavar="PACKAGES",
        help="Extract GitHub release binaries from the tested npm tarballs in PACKAGES",
    )
    parser.add_argument(
        "--release-tag",
        default=os.getenv("BEQUADRO_RELEASE_TAG") or None,
        help="Build the release this tag names, such as v1.2.0, with its version (default: $BEQUADRO_RELEASE_TAG)",
    )
    args = parser.parse_args()

    if args.stage_release_cli is not None:
        if not args.release_tag:
            parser.error("--stage-release-cli requires a release tag")
        stage_release_cli(args.stage_release_cli, root / "artifacts" / "release-cli", args.release_tag)
        return

    if args.release_tag:
        # Inherited by every dotnet command, and read by Directory.Build.props
        os.environ["BEQUADRO_VERSION"] = release_version(args.release_tag)
        print(f"Building version {os.environ['BEQUADRO_VERSION']}", flush=True)

    run("dotnet", "tool", "restore")

    if args.fix:
        run("dotnet", "csharpier", "format", ".")

    if args.check:
        run(sys.executable, "-m", "unittest", "discover", "--start-directory", "tests/build")
        run("dotnet", "csharpier", "check", ".")
        run("dotnet", "build", "Bequadro.sln", "--configuration=Release", "-warnaserror")
        run("dotnet", "test", "--solution", "Bequadro.sln", "--configuration=Release", "--no-build")
        # The packages are left in artifacts/ as --pack leaves them, so --check --pack keeps exactly the ones tested
        check_package(root, pack_package(root, "--no-build"))
        cli = publish_cli(root)
        check_npm(root, pack_npm(root, cli))
        check_fingerprint(root, cli)
        check_typescript(root, cli)
        check_python(root, cli)

    if args.pack and not args.check:
        pack_package(root)
        pack_npm(root, publish_cli(root))

    if args.check_npm and not args.check:
        check_npm(root, pack_npm(root, publish_cli(root)))

    if not (args.fix or args.check or args.pack or args.check_npm):
        run("dotnet", "build", "Bequadro.sln")


if __name__ == "__main__":
    main()
