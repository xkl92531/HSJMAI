from __future__ import annotations

import re
import unittest
from pathlib import Path, PurePosixPath, PureWindowsPath
from xml.etree import ElementTree


ROOT = Path(__file__).resolve().parents[2]
DEPLOY = ROOT / "deploy"
WEB_SOURCE = ROOT / "web" / "src"
WEB_IMPORT_PATTERN = re.compile(
    r"(?:\bfrom\s*|\bimport\s*\(\s*|\bimport\s+)(['\"])([^'\"]+)\1"
)
WEB_RESOLVE_EXTENSIONS = (".d.ts", ".ts", ".tsx", ".js", ".jsx", ".vue", ".json", ".css", ".scss")


def read(relative_path: str) -> str:
    return (ROOT / relative_path).read_text(encoding="utf-8")


def path_uses_exact_casing(base: Path, relative: PurePosixPath) -> bool:
    current = base
    for component in relative.parts:
        if component in ("", "."):
            continue
        if component == "..":
            current = current.parent
            continue
        names = {child.name for child in current.iterdir()} if current.is_dir() else set()
        if component not in names:
            return False
        current /= component
    return current.is_file()


def local_import_candidates(source: Path, specifier: str) -> tuple[Path, list[PurePosixPath]] | None:
    clean = specifier.split("?", 1)[0]
    if clean.startswith("@/"):
        base = WEB_SOURCE
        relative = PurePosixPath(clean[2:])
    elif clean.startswith("#/"):
        base = WEB_SOURCE / "types"
        relative = PurePosixPath(clean[2:])
    elif clean.startswith("./") or clean.startswith("../"):
        base = source.parent
        relative = PurePosixPath(clean)
    else:
        return None

    candidates = [relative]
    if not any(str(relative).endswith(extension) for extension in WEB_RESOLVE_EXTENSIONS):
        candidates.extend(PurePosixPath(f"{relative}{extension}") for extension in WEB_RESOLVE_EXTENSIONS)
        candidates.extend(relative / f"index{extension}" for extension in WEB_RESOLVE_EXTENSIONS)
    return base, candidates


class DeploymentContractTests(unittest.TestCase):
    def test_required_delivery_files_exist(self) -> None:
        required = [
            ".gitattributes",
            ".dockerignore",
            "deploy/docker/api.Dockerfile",
            "deploy/docker/api.offline.Dockerfile",
            "deploy/docker/api/entrypoint.sh",
            "deploy/docker/nuget.offline.config",
            "deploy/compose.yaml",
            "deploy/.env.example",
            "deploy/scripts/common.sh",
            "deploy/scripts/deploy.sh",
            "deploy/scripts/install.sh",
            "deploy/scripts/migrate-appdata.sh",
            "deploy/scripts/server-deploy.sh",
            "deploy/scripts/start.sh",
            "deploy/scripts/stop.sh",
            "deploy/scripts/status.sh",
            "deploy/scripts/logs.sh",
            "deploy/scripts/verify.sh",
            "deploy/scripts/uninstall.sh",
            "deploy/offline/build-bundle.sh",
            "deploy/offline/build-bundle-offline.ps1",
            "deploy/tests/test_port_selection.sh",
            "deploy/README-Linux离线部署.md",
        ]

        missing = [path for path in required if not (ROOT / path).is_file()]
        self.assertEqual([], missing, f"missing deployment files: {missing}")

    def test_compose_runs_only_api_and_packages_no_external_services(self) -> None:
        compose = read("deploy/compose.yaml")
        lowered = compose.lower()

        self.assertIn("services:", compose)
        self.assertRegex(compose, r"(?m)^  api:")
        self.assertNotRegex(compose, r"(?m)^  web:")
        self.assertNotRegex(compose, r"(?m)^  redis:")
        self.assertNotRegex(lowered, r"(?m)^  postgres(?:ql)?:")
        self.assertNotRegex(lowered, r"image:\s*postgres")
        self.assertNotRegex(lowered, r"image:\s*redis")
        self.assertNotIn("5432" + ":5432", compose)
        self.assertNotRegex(lowered, r"(?:initdb|docker-entrypoint-initdb|create database|pg_dump)")
        self.assertNotIn("WebConfig__DBList", compose)
        self.assertIn("WebConfig__RedisServerUrl: ${WebConfig__RedisServerUrl:-127.0.0.1:6379}", compose)
        self.assertNotIn("WebConfig__RedisServerUrl: 192.168.6.131", compose)

    def test_api_uses_linux_host_network_for_local_protected_redis(self) -> None:
        compose = read("deploy/compose.yaml")

        self.assertNotRegex(compose, r"(?m)^    ports:$")
        self.assertRegex(compose, r"(?m)^    network_mode: host$")
        self.assertIn("127.0.0.1:6379", compose)

    def test_start_selects_and_persists_a_free_server_port(self) -> None:
        common = read("deploy/scripts/common.sh")
        start = read("deploy/scripts/start.sh")
        example = read("deploy/.env.example")

        self.assertIn("select_http_port()", common)
        self.assertIn("port_is_available", common)
        self.assertIn("is_listening_port", common)
        self.assertIn("docker_publishes_port", common)
        self.assertIn("candidate=28000", common)
        self.assertIn("candidate<=28999", common)
        self.assertIn("write_env_value HSJM_HTTP_PORT", common)
        self.assertIn('http_port="$(select_http_port)"', start)
        self.assertIn("--force-recreate", start)
        self.assertRegex(example, r"(?m)^HSJM_HTTP_PORT=$")

    def test_one_click_deploy_installs_starts_and_verifies(self) -> None:
        script = read("deploy/scripts/deploy.sh")

        self.assertIn('"$SCRIPT_DIR/install.sh"', script)
        self.assertIn('"$SCRIPT_DIR/start.sh"', script)
        self.assertIn('"$SCRIPT_DIR/verify.sh"', script)
        self.assertIn("deployment_http_port", script)

    def test_container_overrides_legacy_appsettings_url(self) -> None:
        compose = read("deploy/compose.yaml")
        settings = read("api/HSJM.AI/appsettings.json")

        self.assertIn('"urls": "http://*:2405"', settings)
        self.assertRegex(
            compose,
            r"(?m)^      urls: http://\+:\$\{HSJM_HTTP_PORT:\?start\.sh selects a free host port\}$",
        )

    def test_api_hosts_spa_mcp_progress_and_signalr_directly(self) -> None:
        program = read("api/HSJM.AI/Program.cs")

        self.assertIn("app.UseDefaultFiles()", program)
        self.assertIn("app.UseStaticFiles()", program)
        self.assertIn('app.MapMcp("/mcp")', program)
        self.assertIn('app.MapHub<AgentStreamingHub>("/hubs/agent-streaming")', program)
        self.assertIn('app.MapHub<WorkflowHub>("/hubs/workflow")', program)
        self.assertIn('app.MapHub<StationShadowMonitoringHub>("/hubs/station-shadow-monitoring")', program)
        self.assertIn('app.MapFallbackToFile("index.html")', program)
        self.assertGreater(
            program.index('app.MapFallbackToFile("index.html")'),
            program.index('app.MapMcp("/mcp")'),
        )

    def test_mcp_sdk_2_and_modern_protocol_are_locked_together(self) -> None:
        api_project = ElementTree.fromstring(read("api/HSJM.AI/HSJM.AI.csproj"))
        app_layer_project = ElementTree.fromstring(
            read("api/HSJM.AI.AppLayer/HSJM.AI.AppLayer.csproj")
        )
        verify_script = read("deploy/scripts/verify.sh")

        api_versions = {
            item.attrib["Include"]: item.attrib["Version"]
            for item in api_project.findall(".//PackageReference")
            if item.attrib.get("Include", "").startswith("ModelContextProtocol")
        }
        app_layer_versions = {
            item.attrib["Include"]: item.attrib["Version"]
            for item in app_layer_project.findall(".//PackageReference")
            if item.attrib.get("Include", "").startswith("ModelContextProtocol")
        }

        self.assertEqual(
            "2.0.0",
            api_versions.get("ModelContextProtocol.AspNetCore"),
        )
        self.assertEqual(
            "2.0.0",
            app_layer_versions.get("ModelContextProtocol"),
        )
        self.assertIn("2026-07-28", verify_script)
        self.assertIn("server/discover", verify_script)
        self.assertIn("2025-03-26", verify_script)
        self.assertIn("--header 'Mcp-Method: server/discover'", verify_script)
        self.assertIn("--header 'Mcp-Method: tools/list'", verify_script)

        program = read("api/HSJM.AI/Program.cs")
        self.assertIn('Name = "hsjm-diagnosis"', program)
        self.assertIn('Version = "2.0.0"', program)

    def test_obsolete_web_and_nginx_images_are_removed(self) -> None:
        obsolete = [
            ROOT / "deploy/docker/web.Dockerfile",
            ROOT / "deploy/docker/nginx/default.conf",
            ROOT / "deploy/docker/api/appsettings.Docker.json",
        ]

        self.assertEqual([], [str(path.relative_to(ROOT)) for path in obsolete if path.exists()])

    def test_linux_shell_files_are_forced_to_lf(self) -> None:
        attributes = read(".gitattributes")

        self.assertRegex(attributes, r"(?m)^\*\.sh\s+text\s+eol=lf$")
        self.assertRegex(attributes, r"(?m)^\*\.Dockerfile\s+text\s+eol=lf$")

    def test_api_image_contains_linux_runtime_and_safe_settings(self) -> None:
        dockerfile = read("deploy/docker/api.Dockerfile")
        settings_text = read("api/HSJM.AI/appsettings.json")

        self.assertIn("FROM node:22-alpine AS web-build", dockerfile)
        self.assertIn("mcr.microsoft.com/dotnet/sdk:10.0-noble", dockerfile)
        self.assertIn("mcr.microsoft.com/dotnet/aspnet:10.0-noble", dockerfile)
        self.assertIn("COPY web/package.json web/pnpm-lock.yaml web/.npmrc ./", dockerfile)
        self.assertIn("pnpm install --frozen-lockfile", dockerfile)
        self.assertIn("pnpm --config.verify-deps-before-run=false build", dockerfile)
        self.assertIn("test -f dist/index.html", dockerfile)
        self.assertIn("COPY --from=web-build --chown=app:app /src/dist/ /opt/hsjm/web.seed/", dockerfile)
        self.assertIn("python3 -m venv /opt/venv", dockerfile)
        self.assertIn("/opt/venv/bin/python", dockerfile)
        self.assertIn("playwright install --with-deps chromium", dockerfile)
        self.assertRegex(dockerfile, r"(?m)^USER app$")
        self.assertNotIn("appsettings.Docker.json", dockerfile)
        self.assertNotRegex(dockerfile, r"rm\s+-f[^\n]*appsettings\.json")
        self.assertNotRegex(
            dockerfile,
            r"chown\s+-R[^\n]*(?:/ms-playwright|/opt/venv)",
            "recursive chown duplicates the heavyweight Python/Chromium layers",
        )
        self.assertIn("install -d -o app -g app", dockerfile)
        self.assertIn('"ConfigId": "DefultDataBase"', settings_text)
        self.assertIn('"DatabaseType": "PostgreSQL"', settings_text)
        self.assertIn("HOST=192.168.6.75", settings_text)
        self.assertIn('"RedisServerUrl": "192.168.6.75:6379"', settings_text)
        self.assertIn("Database=hsjm_ai_new", settings_text)
        self.assertNotIn("hsjm_ai_new_test", settings_text)

    def test_image_build_reuses_dependency_downloads_across_network_retries(self) -> None:
        dockerfile = read("deploy/docker/api.Dockerfile")

        self.assertIn(
            "--mount=type=cache,id=hsjm-pnpm-store,target=/pnpm/store",
            dockerfile,
        )
        self.assertIn("pnpm config set store-dir /pnpm/store", dockerfile)
        nuget_cache_mount = "--mount=type=cache,id=hsjm-nuget-packages,target=/root/.nuget/packages"
        self.assertGreaterEqual(dockerfile.count(nuget_cache_mount), 2)
        publish_position = dockerfile.index("dotnet publish")
        publish_run_position = dockerfile.rfind("RUN ", 0, publish_position)
        self.assertIn(nuget_cache_mount, dockerfile[publish_run_position:publish_position])
        self.assertIn(
            "--mount=type=cache,id=hsjm-pip-cache,target=/root/.cache/pip",
            dockerfile,
        )
        self.assertNotIn("pip install --no-cache-dir", dockerfile)

    def test_online_source_defaults_and_archive_exports_are_safe(self) -> None:
        attributes = read(".gitattributes")
        core_config = read("api/HSJM.AI.Core/Configs/AppsettingsConfig.cs")

        self.assertIn("api/HSJM.AI/appsettings*.json export-ignore", attributes)
        self.assertIn("api/docs/*.md export-ignore", attributes)
        self.assertIn("api/docs/sql export-ignore", attributes)
        self.assertNotIn("192" + ".168.6.146", core_config)
        self.assertNotIn("jin" + "mings", core_config)

    def test_web_build_is_merged_into_api_as_same_origin_bundle(self) -> None:
        production_env = read("web/.env.production")

        dockerfile = read("deploy/docker/api.Dockerfile")
        self.assertIn("FROM node:22-alpine AS web-build", dockerfile)
        self.assertIn("pnpm install --frozen-lockfile", dockerfile)
        self.assertIn("test -f dist/index.html", dockerfile)
        self.assertIn("COPY --from=web-build --chown=app:app /src/dist/ /opt/hsjm/web.seed/", dockerfile)
        self.assertRegex(production_env, r"(?m)^VITE_APP_API_BASEURL\s*=\s*$")

    def test_early_configuration_accepts_environment_overrides(self) -> None:
        program = read("api/HSJM.AI/Program.cs")
        init_config = program.split("private static void InitConfig()", 1)[1]

        self.assertIn(".AddEnvironmentVariables()", init_config)
        self.assertLess(
            init_config.index('.AddJsonFile("appsettings.AIAgent.json"'),
            init_config.index(".AddEnvironmentVariables()"),
        )

    def test_external_database_is_read_only_during_deployment_startup(self) -> None:
        program = read("api/HSJM.AI/Program.cs")
        health_check = read("api/HSJM.AI/Health/ExternalPostgresHealthCheck.cs")
        settings_text = read("api/HSJM.AI/appsettings.json")

        self.assertIn('GetValue("WebConfig:Hangfire:PrepareSchemaIfNecessary", false)', program)
        self.assertIn('"PrepareSchemaIfNecessary": false', settings_text)
        self.assertIn("AddCheck<ExternalPostgresHealthCheck>", program)
        self.assertIn('MapHealthChecks("/health")', program)
        self.assertIn("SELECT 1", health_check)
        self.assertNotRegex(health_check.lower(), r"(?:create|alter|drop|insert|update|delete)\s+")
        self.assertIn("StringComparison.OrdinalIgnoreCase", health_check)

    def test_platform_readiness_checks_postgresql_and_redis_protocols(self) -> None:
        program = read("api/HSJM.AI/Program.cs")
        redis_check = read("api/HSJM.AI/Health/ExternalRedisHealthCheck.cs")

        self.assertIn("AddCheck<ExternalPostgresHealthCheck>", program)
        self.assertIn("AddCheck<ExternalRedisHealthCheck>", program)
        self.assertIn('MapHealthChecks("/live"', program)
        self.assertIn("PingAsync", redis_check)

    def test_mcp_requires_authenticated_remote_client(self) -> None:
        program = read("api/HSJM.AI/Program.cs")

        self.assertIn('AddPolicy("McpAccess"', program)
        self.assertIn('MapMcp("/mcp").RequireAuthorization("McpAccess")', program)

    def test_docker_upserts_named_mcp_manager_service(self) -> None:
        compose = read("deploy/compose.yaml")
        program = read("api/HSJM.AI/Program.cs")
        planner = read("api/HSJM.AI/Mcp/McpDiagnosisRegistrationPlanner.cs")
        hosted = read("api/HSJM.AI/Mcp/McpDiagnosisRegistrationHostedService.cs")

        self.assertIn('McpDiagnosisRegistration__Enabled: "true"', compose)
        self.assertIn("McpDiagnosisRegistration__Endpoint:", compose)
        self.assertIn('GetValue("McpDiagnosisRegistration:Enabled", false)', program)
        self.assertIn('ServerName = "hsjm-diagnosis"', planner)
        self.assertIn('keeper.ServerType = "HTTP"', planner)
        self.assertIn("linkedTools.ForEach(item => item.McpServerID = plan.Keeper.Id)", hosted)
        self.assertIn("SoftDeleteByIdsAsync", hosted)

    def test_project_references_use_exact_linux_path_casing(self) -> None:
        projects = list((ROOT / "api").glob("*.*/**/*.csproj"))
        self.assertGreaterEqual(len(projects), 7)

        errors: list[str] = []
        for project in projects:
            tree = ElementTree.parse(project)
            for reference in tree.findall(".//ProjectReference"):
                include = reference.attrib["Include"]
                current = project.parent
                for component in PureWindowsPath(include).parts:
                    if component == "..":
                        current = current.parent
                        continue
                    names = {child.name for child in current.iterdir()}
                    if component not in names:
                        errors.append(
                            f"{project.relative_to(ROOT)} -> {include}: "
                            f"'{component}' does not match exact casing under {current.relative_to(ROOT)}"
                        )
                        break
                    current /= component

        self.assertEqual([], errors, "\n".join(errors))

    def test_solution_project_paths_use_exact_linux_casing(self) -> None:
        solution = read("api/HSJM.AI.sln")
        errors: list[str] = []
        for include in re.findall(r'^Project\("[^"]+"\) = "[^"]+", "([^"]+\.csproj)"', solution, re.M):
            current = ROOT / "api"
            for component in PureWindowsPath(include).parts:
                names = {child.name for child in current.iterdir()}
                if component not in names:
                    errors.append(f"api/HSJM.AI.sln -> {include}: wrong case '{component}'")
                    break
                current /= component

        self.assertEqual([], errors, "\n".join(errors))

    def test_web_local_imports_use_exact_linux_path_casing(self) -> None:
        errors: list[str] = []
        source_files = [
            path
            for path in WEB_SOURCE.rglob("*")
            if path.suffix in {".ts", ".tsx", ".js", ".jsx", ".vue"}
        ]

        for source in source_files:
            content = source.read_text(encoding="utf-8")
            for match in WEB_IMPORT_PATTERN.finditer(content):
                specifier = match.group(2)
                resolved = local_import_candidates(source, specifier)
                if resolved is None:
                    continue
                base, candidates = resolved
                if any(path_uses_exact_casing(base, candidate) for candidate in candidates):
                    continue
                errors.append(f"{source.relative_to(ROOT)} -> {specifier}")

        self.assertEqual([], errors, "Linux path mismatch or missing local import:\n" + "\n".join(errors))

    def test_offline_builder_saves_only_api_runtime_image(self) -> None:
        builder = read("deploy/offline/build-bundle.sh")
        lowered = builder.lower()

        self.assertIn("docker buildx build", builder)
        self.assertIn("--platform linux/amd64", builder)
        self.assertIn("docker image save", builder)
        self.assertIn("hsjm-ai-api", builder)
        self.assertNotIn("hsjm-ai-web", builder)
        self.assertNotIn("redis:7.4-alpine", builder)
        self.assertNotIn("docker pull", builder)
        self.assertNotIn("postgres", lowered)
        self.assertIn("SHA256SUMS", builder)
        self.assertIn("HSJM-MCP流式诊断与Docker部署手册.docx", builder)
        self.assertNotIn("HSJM-6.131-Docker部署与MCP调用手册.docx", builder)

    def test_runtime_scripts_are_strict_and_do_not_dump_secrets(self) -> None:
        scripts = list((DEPLOY / "scripts").glob("*.sh"))
        scripts.append(DEPLOY / "offline" / "build-bundle.sh")
        self.assertTrue(all(path.is_file() for path in scripts))

        for path in scripts:
            content = path.read_text(encoding="utf-8")
            self.assertTrue(content.startswith("#!/usr/bin/env bash"), path)
            self.assertIn("set -Eeuo pipefail", content, path)
            self.assertNotRegex(content, r"(?m)^(?:set -x|env|printenv)\s*$", path)

    def test_example_environment_contains_no_real_credentials(self) -> None:
        example = read("deploy/.env.example")

        for required in ["HSJM_HTTP_PORT=", "HSJM_TIME_ZONE=", "HSJM_REDIS_CONNECTION="]:
            self.assertIn(required, example)

        for forbidden in ["Password=", "ApiKey=", "SecurityKey="]:
            self.assertNotIn(forbidden, example)

        self.assertNotIn("jm123", example)

    def test_optional_env_maps_authenticated_redis_without_rebuilding_image(self) -> None:
        common = read("deploy/scripts/common.sh")

        self.assertIn("env_value HSJM_REDIS_CONNECTION", common)
        self.assertIn('export WebConfig__RedisServerUrl="$redis_override"', common)


if __name__ == "__main__":
    unittest.main(verbosity=2)
