"""Tests of build.py's own logic, which build.py --check runs first with unittest."""

import importlib.util
import io
import json
import tarfile
import tempfile
import unittest
from pathlib import Path

_spec = importlib.util.spec_from_file_location("build", Path(__file__).parents[2] / "build.py")
assert _spec is not None and _spec.loader is not None
build = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(build)


class ReleaseVersionTests(unittest.TestCase):
    """CON-007: a release tag names a version that both npm's semver and NuGet accept."""

    def test_accepts_versions_and_prereleases(self) -> None:
        for tag, version in [
            ("v0.1.0", "0.1.0"),
            ("v1.2.0", "1.2.0"),
            ("v10.20.30", "10.20.30"),
            ("v1.2.0-rc.1", "1.2.0-rc.1"),
            ("v1.2.0-0", "1.2.0-0"),
            ("v1.2.0-alpha", "1.2.0-alpha"),
            ("v1.2.0-alpha.0.beta", "1.2.0-alpha.0.beta"),
            ("v1.2.0-01a", "1.2.0-01a"),
            ("v1.2.0--", "1.2.0--"),
            ("v1.2.0-x-y.7", "1.2.0-x-y.7"),
            ("v2147483647.0.0", "2147483647.0.0"),
            ("v0.0.0-unreleased.1", "0.0.0-unreleased.1"),
        ]:
            with self.subTest(tag=tag):
                self.assertEqual(build.release_version(tag), version)

    def test_rejects_what_either_registry_rejects(self) -> None:
        for tag in [
            # Not the format
            "1.2.0",
            "v1.2",
            "v1.2.0.0",
            "V1.2.0",
            "v1.2.0-",
            "v1.2.0-rc.",
            "v1.2.0-rc..1",
            "v1.2.0-rc_1",
            "v1.2.0+build",
            "v1.2.0-rc.1+build",
            " v1.2.0",
            # Leading zeros, which semantic versioning forbids in numbers and numeric prerelease identifiers
            "v01.2.0",
            "v1.02.0",
            "v1.2.00",
            "v1.2.0-01",
            "v1.2.0-rc.01",
            "v1.2.0-00",
            # NuGet's version numbers are 32-bit
            "v2147483648.0.0",
            # nuget.org's limit on a version's length
            "v1.0.0-" + "a" * 59,
            # The version of every build that isn't a release
            "v0.0.0-unreleased",
        ]:
            with self.subTest(tag=tag):
                with self.assertRaises(Exception):
                    build.release_version(tag)

    def test_longest_version_nuget_org_takes(self) -> None:
        tag = "v1.0.0-" + "a" * 58
        self.assertEqual(len(build.release_version(tag)), 64)


class ReleaseCliTests(unittest.TestCase):
    """CLI-001: GitHub gets the tested standalone executable for each platform."""

    version = "1.2.0-rc.1"
    tag = f"v{version}"
    assets = {
        "darwin-arm64": "bequadro-osx-arm64",
        "darwin-x64": "bequadro-osx-x64",
        "linux-arm64": "bequadro-linux-arm64",
        "linux-x64": "bequadro-linux-x64",
        "win32-arm64": "bequadro-win-arm64.exe",
        "win32-x64": "bequadro-win-x64.exe",
    }

    def test_stages_each_platform_binary(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            packages = Path(temporary)
            output = packages / "assets"
            for platform in self.assets:
                self.write_package(packages, platform, self.version)

            build.stage_release_cli(packages, output, self.tag)

            self.assertEqual(
                {asset.name: asset.read_bytes() for asset in output.iterdir()},
                {asset: platform.encode() for platform, asset in self.assets.items()},
            )

    def test_rejects_mislabeled_version(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            packages = Path(temporary)
            for platform in self.assets:
                version = "1.2.0" if platform == "darwin-arm64" else self.version
                self.write_package(packages, platform, version)

            with self.assertRaisesRegex(ValueError, "wrong package name or version"):
                build.stage_release_cli(packages, packages / "assets", self.tag)

    def test_requires_every_platform(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            packages = Path(temporary)
            for platform in self.assets.keys() - {"win32-x64"}:
                self.write_package(packages, platform, self.version)

            with self.assertRaisesRegex(FileNotFoundError, "bequadro-win32-x64"):
                build.stage_release_cli(packages, packages / "assets", self.tag)

    def write_package(self, packages: Path, platform: str, manifest_version: str) -> None:
        binary = "bequadro.exe" if platform.startswith("win32-") else "bequadro"
        manifest = {"name": f"@bequadro/{platform}", "version": manifest_version}
        with tarfile.open(packages / f"bequadro-{platform}-{self.version}.tgz", "w:gz") as archive:
            for name, data in [
                ("package/package.json", json.dumps(manifest).encode()),
                (f"package/bin/{binary}", platform.encode()),
            ]:
                entry = tarfile.TarInfo(name)
                entry.size = len(data)
                archive.addfile(entry, io.BytesIO(data))


if __name__ == "__main__":
    unittest.main()
