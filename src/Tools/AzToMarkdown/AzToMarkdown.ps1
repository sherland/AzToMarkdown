<#
.SYNOPSIS
    Maps an entire Azure tenant topology to an Obsidian-compatible Markdown vault.

.DESCRIPTION
    Fetches all Azure resources across all subscriptions (or a single subscription)
    using bulk Azure Resource Graph queries, builds a directed dependency graph
    in memory, and writes one Markdown file per rendered resource under the vault
    root. Role-assignment nodes are embedded in their scoped resource or the root
    assignment file.

    Each resource file contains schema-v1 YAML front matter with canonical identity,
    lossless Azure metadata, normalized relationships, and role assignments. Its
    Markdown body contains human-readable details and WikiLinks for Obsidian navigation.

    Requires: az CLI authenticated (az login), resource-graph extension
    (auto-installed if missing).

.PARAMETER Output
    Root directory of the generated vault.
    Defaults to .\vault in the current directory.

.PARAMETER Subscription
    Azure subscription ID to scope all queries.
    Omit to scan all subscriptions visible to the logged-in account.

.EXAMPLE
    .\AzToMarkdown.ps1 -Output C:\vaults\my-tenant
    # Scans all subscriptions and writes the vault to C:\vaults\my-tenant

.EXAMPLE
    .\AzToMarkdown.ps1 -Subscription "00000000-0000-0000-0000-000000000000"
    # Scans a single subscription
#>
param(
    [string] $Output       = "",
    [string] $Subscription = "",
    [switch] $Help
)

$ErrorActionPreference = "Stop"

if ($Help) {
    Get-Help $MyInvocation.MyCommand.Path -Detailed
    exit 0
}

# ── Locate the compiled binary ───────────────────────────────────────────────
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $scriptDir "bin\Debug\net10.0\AzToMarkdown.exe"

if (-not (Test-Path $exe)) {
    $exe = Join-Path $scriptDir "bin\Release\net10.0\AzToMarkdown.exe"
}

if (-not (Test-Path $exe)) {
    Write-Host "Binary not found — building AzToMarkdown…" -ForegroundColor Yellow
    $proj = Join-Path $scriptDir "AzToMarkdown.csproj"
    dotnet build $proj --configuration Debug --nologo -v quiet
    $exe = Join-Path $scriptDir "bin\Debug\net10.0\AzToMarkdown.exe"
    if (-not (Test-Path $exe)) {
        Write-Error "Build failed. Check dotnet build output above."
        exit 1
    }
}

# ── Build argument list ───────────────────────────────────────────────────────
$argList = @()

if ($Output)       { $argList += "--output";       $argList += $Output }
if ($Subscription) { $argList += "--subscription"; $argList += $Subscription }

# ── Run ───────────────────────────────────────────────────────────────────────
& $exe @argList
exit $LASTEXITCODE
