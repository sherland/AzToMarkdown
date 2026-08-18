# AzToMarkdown – Repository Instructions

**AzToMarkdown** is a .NET 10 tool-suite built around a CLI that queries Azure
Resource Graph (ARG) and maps an entire Azure tenant topology into an Obsidian-compatible Markdown
vault with lossless YAML front-matter.

---

## Solution Layout

```
AzToMarkdown.slnx                       ← VS solution
Directory.Packages.props                ← Central packages for AzToMarkdown-owned projects
src/
  AzResourceDetails.Templating/         ← Synchronized portal-compatible template runtime
  Libraries/
    AzToMarkdown.Core/                  ← Class library (tenant graph + vault logic)
  Tools/
    AzToMarkdown/                       ← CLI over Core (outputs to a "vault" folder)
tests/
  AzResourceDetails.Templating.Tests/   ← Synchronized runtime unit tests
  AzToMarkdown.Tests/                   ← MSTest — vault serializer/writer/reader unit tests
  AzToMarkdown.ScenarioTests/           ← MSTest integration — az-login-backed live tests + tracing
config/azure-locations.json              ← Synchronized Azure-region reference data
scripts/Sync-AzResourceDetailsTemplating.ps1
                                        ← Copies/checks the authoritative sibling project
```

---

## Build & Run Commands

```bash
# Build everything
dotnet build AzToMarkdown.slnx

# Run the CLI
.\src\Tools\AzToMarkdown\AzToMarkdown.ps1 -Output C:\vaults\my-tenant

# Synchronized shared-runtime tests:
dotnet test tests/AzResourceDetails.Templating.Tests

# Unit tests (fast, no Azure needed — serializer, vault, offline query client, core behavior):
dotnet test tests/AzToMarkdown.Tests --filter "TestCategory=Unit"

# Scenario/integration tests (real ARG queries — require az login):
dotnet test tests/AzToMarkdown.ScenarioTests --filter TestCategory=Integration

# AzureLive test (creates + deletes real low-cost resources in westeurope by default; needs az login
# WITH resource-group write permission — skips as Inconclusive otherwise):
dotnet test tests/AzToMarkdown.ScenarioTests --filter TestCategory=AzureLive
# Set AZTOMARKDOWN_LIVE_LOCATION to override westeurope when subscription policy requires another region.

# NOTE: MSTest filter uses TestCategory= (not Category=)
# NOTE: After Core changes use --no-incremental to avoid stale incremental DLLs:
#       dotnet build AzToMarkdown.slnx --no-incremental
```

---

## Project Details

### AzResourceDetails.Templating  (synchronized library)

Portal-compatible Scriban model construction, formatting functions, friendly labels, SKU/version
helpers, and runtime contract shared with `AzResourceDetailsDownloader`. Its complete source project,
test project, and region data are byte-identical mirrors; see **Shared template runtime ownership**
below before changing anything in those paths.

### AzToMarkdown.Core  (library)

All reusable tenant-enumeration and vault logic. Key namespaces:

| Namespace | Purpose |
|-----------|---------|
| `Abstractions/` | `IArgQueryClient`, `IProgressReporter` |
| `Azure/` | `AzCliQueryClient`, `ThrottlingRetryQueryClient`, `TenantEnumerator`, `RelationshipExtractor` |
| `Models/` | `ArmId`, `JsonPath`, `TenantGraph` |
| `Rendering/` | `VaultTemplateEngine`, `VaultWriter` |
| `Vault/` | `FrontMatterSerializer`, `YamlJsonConverter`, `VaultReader`, `VaultIndex`, `VaultQueryClient` |
| `Diagnostics/` | `CoreActivity` (OTel `ActivitySource`) |

DI registration: `services.AddAzToMarkdownCore(subscriptionId?)`.

### AzToMarkdown (CLI)

A CLI wrapper over `AzToMarkdown.Core` that maps an entire Azure tenant to an Obsidian
Markdown vault. Defaults output to a `vault` folder in the current directory.

Fetch → build graph → write vault, in three steps (`Program.cs`):
1. `TenantEnumerator.FetchAllAsync()` — three bulk ARG queries across all (or one) subscription, plus ACR repository enumeration.
2. `RelationshipExtractor.Build(nodes)` — pure in-memory graph construction, no I/O.
3. `VaultWriter.WriteAll(graph, subNames, outputPath)` — one `.md` file per rendered resource; role-assignment nodes are embedded in scoped resource files or `_role_assignments.md`.

#### Vault schema v1 (lossless master data)

Every generated `.md` file carries machine-generated YAML front-matter (never Scriban/string-built — `FrontMatterSerializer` + YamlDotNet):

- `schema_version: 1` + `aztomarkdown_version` — readers must reject higher schema versions.
- Obsidian-friendly flat keys (`id`, `name`, `type`, `resource-group`, `location`, `version`, per-type extras like `sku`/`fqdn` contributed by templates via `extra_fm`).
- `resource:` — canonical identity (id, name, type, subscription_id/name, resource_group, location).
- `azure_metadata:` — the complete, value-faithful ARG `properties` bag, `tags`, and optional top-level `kind`, `sku`, and `identity`. Strings are always double-quoted; numbers retain their raw JSON text as plain scalars, so round-trip typing is unambiguous.
- `relationships:` — normalized graph edges `{id, name?, type?, direction, label}`.
- `role_assignments:` — lossless embedded assignments; subscription/RG-scoped ones land in `_role_assignments.md`.
- `_summary.md` front-matter carries the `subscriptions:` id→name map (needed for offline consumption).

Key types (all in `AzToMarkdown.Core/Vault/`): `FrontMatterSerializer`, `YamlJsonConverter` (lossless JSON⇄YAML + `JsonDeepEquals`), `VaultReader` (vault → `TenantNode`s), `VaultIndex` + `VaultQueryClient` (offline `IArgQueryClient` used by the offline-replay pipeline and tests).

Normative docs: `docs/ARCHITECTURE.md` §7 (schema) and `docs/metadata-reference.md`.

### AzToMarkdown.ScenarioTests  (integration + tracing tests)

MSTest project that tests `AzToMarkdown.Core` directly. Requires `az login`.

| Class | Purpose |
|-------|---------|
| `AzToMarkdownLiveVaultTests` | Creates real low-cost Azure resources, runs the full AzToMarkdown pipeline (`TenantEnumerator` → `RelationshipExtractor` → `VaultWriter`) against them, and asserts lossless round-trip through the vault — including an offline-replay pass via `VaultQueryClient`. `TestCategory=AzureLive`. |
| `AzCliTracingTests` | Verifies representative live `AzCliQueryClient` spans, tags, error status, and exception events. `TestCategory=Integration`. |

---

## Key CLI Flags

`AzToMarkdown` (`Program.cs`):

| Flag | Description |
|------|-------------|
| `--output <path>` | Root folder for the generated vault (default: `./vault`) |
| `--subscription <id>` | Scope queries to a single subscription (default: all) |
| `--help`, `-h` | Show usage |

PowerShell wrapper (`AzToMarkdown.ps1`) exposes the same options as `-Output`, `-Subscription`.

---

## Key Conventions

- **Authentication**: `az login` required; ensure the caller is signed in before running.
- **Shared template runtime ownership**: `AzResourceDetailsDownloader` is the authoritative
  source for `src/AzResourceDetails.Templating/`,
  `tests/AzResourceDetails.Templating.Tests/`, and `config/azure-locations.json`. These paths are
  synchronized mirrors and must not be edited in this repository. Make required runtime changes in
  the sibling repository first, then import them with
  `scripts/Sync-AzResourceDetailsTemplating.ps1`; use its `-Check` switch to verify byte-for-byte
  parity. AzToMarkdown-specific adapters and integration tests outside the mirrored paths remain
  maintained here. If a shared-runtime change is needed while working in this repository, provide
  a concrete implementation prompt for the sibling repository instead of modifying the mirror.
- **No caching**: every run queries Azure live. `TenantEnumerator` issues each query exactly once
  per run, so there is no within-run repeat-query benefit to cache; a query cache would only help
  rapid, repeated re-runs of the whole CLI, which isn't the tool's normal usage pattern.
- **DI**: All Core services registered via `AddAzToMarkdownCore()`. The CLI overrides `IProgressReporter` with `SpectreProgressReporter`.
- Target frameworks: `net10.0` for all projects.

---

## ⚠️ Mandatory Test Run After Changes

**You MUST run the relevant tests after any change that can have side-effects. Do not skip this step.**

### Which tests to run

| Change type | Tests required |
|-------------|---------------|
| Synchronized runtime or its integration | Shared-runtime tests **AND** AzToMarkdown unit tests |
| Any change to `AzToMarkdown.Core` (queries, enumeration, relationship extraction, vault rendering) | Unit tests **AND** scenario/integration tests |
| Pure rendering/label changes with no logic impact | Unit tests minimum |

### Commands

```bash
# 0. Shared template runtime:
dotnet test tests/AzResourceDetails.Templating.Tests --logger "console;verbosity=normal"

# 1. Unit tests (fast, no Azure needed — serializer, vault, offline query client, core behavior):
dotnet test tests/AzToMarkdown.Tests --filter "TestCategory=Unit" --logger "console;verbosity=normal"

# 2. Scenario/integration tests — real ARG queries, require az login:
dotnet test tests/AzToMarkdown.ScenarioTests --filter TestCategory=Integration --logger "console;verbosity=normal"

# 3. AzureLive test — creates/deletes real resources, requires az login with RG-write permission:
dotnet test tests/AzToMarkdown.ScenarioTests --filter TestCategory=AzureLive --logger "console;verbosity=normal"
```

**All must pass before claiming a change is complete.** If an integration test is inconclusive (Azure resource not found, no az login), that is acceptable — a failure is not.

---

## OpenTelemetry Tracing — AzCli Spans

`AzCliQueryClient` emits `ActivityKind.Client` spans for every live Azure call.

**Source name**: `"AzToMarkdown.AzCli"` (`AzCliQueryClient.ActivitySourceName`)

| Span name | Trigger | Key tags |
|-----------|---------|----------|
| `azure_resource_graph query` | `RunQueryAsync` | `db.system=azure_resource_graph`, `db.query.text` (≤1 000 chars), `az.result.count` |
| `az resource show` | `GetResourceByIdAsync` | `http.request.method=GET`, `az.resource.id`, `http.response.status_code` |
| `az rest GET` | `GetResourceByIdAsync(useRestPath:true)` | `url.full`, `http.response.status_code` |
| `az rest batch GET` | `BatchArmGetAsync` | `az.batch.request_count`, `http.response.status_code` |
| `az aks command invoke` | `RunAksCommandAsync` | `az.aks.resource_group`, `az.aks.cluster_name`, `az.aks.command` |
| `az acr repository list` | `ListAcrRepositoriesAsync` | `az.acr.registry_name`, `az.subscription.id`, `az.result.count` |
| `az account list` | `FetchSubscriptionNamesAsync` | `az.account.count` |

Failures set `ActivityStatusCode.Error` and record an `exception` event via `Activity.AddEvent` (no `OpenTelemetry` NuGet package dependency in Core).

**Test listener setup** — must set **both** sample delegates (root activities with no parent use `SampleUsingParentId`; omitting it makes `StartActivity` return null):

```csharp
var listener = new ActivityListener
{
    ShouldListenTo      = src => src.Name == AzCliQueryClient.ActivitySourceName,
    Sample              = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    SampleUsingParentId = (ref ActivityCreationOptions<string> _)         => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStarted     = a => started.Add(a),
    ActivityStopped     = a => stopped.Add(a),
};
ActivitySource.AddActivityListener(listener);
```

**Async timing**: for normally-completing async methods the C# state machine disposes `using var activity` *after* the Task completes — `ActivityStopped` can fire after the caller's continuation. Assert on `started` for pre-await tags; poll `stopped` with a short timeout for post-await tags. Exception paths are unaffected.

---

## NuGet Sources

```
nuget.org: https://api.nuget.org/v3/index.json
```

Key dependencies: `Spectre.Console`, `CliWrap`, `Microsoft.Extensions.DependencyInjection`, `YamlDotNet`, `Scriban`.

