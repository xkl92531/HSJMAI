from __future__ import annotations

import hashlib
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "deploy" / "offline" / "build-bundle-offline.ps1"

IMAGE_ARCHIVES = (
    "dockerfile-1.7-linux-amd64.tar",
    "dotnet-aspnet-10.0-noble-linux-amd64.tar",
    "dotnet-sdk-10.0-noble-linux-amd64.tar",
    "hsjm-runtime-deps-20260903-linux-amd64.tar",
    "hsjm-web-deps-pnpm-10.26.2-linux-amd64.tar",
    "node-22-alpine-linux-amd64.tar",
)

SOURCE_INPUTS = (
    "deploy/docker/api.Dockerfile",
    "web/package.json",
    "web/pnpm-lock.yaml",
    "web/.npmrc",
    "api/HSJM.AI/wwwroot/skills/plc-analyzer/scripts/requirements.txt",
    "api/HSJM.AI/wwwroot/skills/pdf-reader/scripts/requirements.txt",
    "api/HSJM.AI/wwwroot/skills/python-sandbox/scripts/requirements.txt",
    "api/HSJM.AI/wwwroot/skills/web-data-search/scripts/requirements.txt",
    "api/HSJM.AI/wwwroot/skills/web-browser/requirements.txt",
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def sha256_with_lf(path: Path) -> str:
    normalized = path.read_bytes().replace(b"\r\n", b"\n")
    return hashlib.sha256(normalized).hexdigest().upper()


class OfflineBuildTests(unittest.TestCase):
    def create_valid_cache(self, root: Path) -> None:
        image_dir = root / "docker" / "images"
        image_dir.mkdir(parents=True)
        image_sums: list[str] = []
        for name in IMAGE_ARCHIVES:
            archive = image_dir / name
            archive.write_bytes(f"fixture:{name}".encode())
            image_sums.append(f"{sha256(archive)}  {name}")
        (image_dir / "SHA256SUMS.txt").write_text(
            "\n".join(image_sums) + "\n", encoding="utf-8"
        )

        (root / "nuget" / "packages").mkdir(parents=True)
        nuget_package = (
            root / "nuget" / "hsjm-packages" / "example" / "1.0.0" / "example.1.0.0.nupkg"
        )
        nuget_package.parent.mkdir(parents=True)
        nuget_package.write_bytes(b"fixture:nuget-package")
        (root / "nuget" / "HSJM-PACKAGES-SHA256SUMS.txt").write_text(
            f"{sha256(nuget_package)}  example/1.0.0/example.1.0.0.nupkg\n",
            encoding="utf-8",
        )
        (root / "pnpm" / "physical-store" / "v10").mkdir(parents=True)
        (root / "python" / "wheels").mkdir(parents=True)

        source_sums = [f"{sha256_with_lf(ROOT / path)}  {path}" for path in SOURCE_INPUTS]
        (root / "HSJM-CACHE-MANIFEST.txt").write_text(
            "PreparedAt=2026-09-03T15:37:41+08:00\n"
            "SourceCommit=fixture\n"
            "Platform=linux/amd64\n\n"
            + "\n".join(source_sums)
            + "\n",
            encoding="utf-8",
        )

    def test_validate_only_accepts_complete_matching_cache(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            cache_root = Path(temp_dir) / "sdk"
            self.create_valid_cache(cache_root)

            result = subprocess.run(
                [
                    "pwsh",
                    "-NoProfile",
                    "-NonInteractive",
                    "-File",
                    str(SCRIPT),
                    "-Version",
                    "test",
                    "-SdkCacheRoot",
                    str(cache_root),
                    "-ValidateOnly",
                ],
                cwd=ROOT,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("OFFLINE_CACHE_VALID=1", result.stdout)

    def test_validate_only_rejects_missing_hsjm_nuget_subset(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            cache_root = Path(temp_dir) / "sdk"
            self.create_valid_cache(cache_root)
            (
                cache_root
                / "nuget"
                / "hsjm-packages"
                / "example"
                / "1.0.0"
                / "example.1.0.0.nupkg"
            ).unlink()
            (cache_root / "nuget" / "hsjm-packages" / "example" / "1.0.0").rmdir()
            (cache_root / "nuget" / "hsjm-packages" / "example").rmdir()
            (cache_root / "nuget" / "hsjm-packages").rmdir()

            result = subprocess.run(
                [
                    "pwsh",
                    "-NoProfile",
                    "-NonInteractive",
                    "-File",
                    str(SCRIPT),
                    "-Version",
                    "test",
                    "-SdkCacheRoot",
                    str(cache_root),
                    "-ValidateOnly",
                ],
                cwd=ROOT,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("CACHE_ERROR=missing-directory", result.stderr)

    def test_validate_only_rejects_missing_hsjm_nuget_checksum_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            cache_root = Path(temp_dir) / "sdk"
            self.create_valid_cache(cache_root)
            (cache_root / "nuget" / "HSJM-PACKAGES-SHA256SUMS.txt").unlink()

            result = subprocess.run(
                [
                    "pwsh",
                    "-NoProfile",
                    "-NonInteractive",
                    "-File",
                    str(SCRIPT),
                    "-Version",
                    "test",
                    "-SdkCacheRoot",
                    str(cache_root),
                    "-ValidateOnly",
                ],
                cwd=ROOT,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("CACHE_ERROR=missing-manifest", result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
