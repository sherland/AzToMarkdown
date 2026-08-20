[CmdletBinding()]
param(
    [string] $SourceRepository,
    [switch] $Check
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($SourceRepository)) {
    $SourceRepository = Join-Path (Split-Path $repositoryRoot -Parent) 'AzResourceDetailsDownloader'
}
$SourceRepository = [IO.Path]::GetFullPath($SourceRepository)

$sourceDirectory      = Join-Path $SourceRepository 'templates'
$destinationDirectory = Join-Path $repositoryRoot 'src\Libraries\AzToMarkdown.Core\Rendering\PortalTemplates'

if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
    throw "Required ARDL templates directory not found at '$sourceDirectory'. Pass -SourceRepository explicitly if the repositories are not siblings."
}

# This is a one-way mirror of ARDL's mechanically-generated portal templates, used by
# VaultTemplateEngine only as a fallback tier when no hand-crafted AzToMd template exists for a
# type (see AGENTS.md "Portal-template fallback tier"). Unlike src/AzResourceDetails.Templating/,
# nothing here is edited by hand in this repository between syncs — that's the whole point of a
# mirror — but AzToMd is also not the authoritative owner of curation quality; ARDL is.

function Get-ContentHash([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.sbn' -File | Sort-Object Name)
if ($sourceFiles.Count -eq 0) {
    throw "No .sbn templates found in '$sourceDirectory'."
}

$mappings = foreach ($sourceFile in $sourceFiles) {
    [pscustomobject]@{
        Source      = $sourceFile.FullName
        Destination = Join-Path $destinationDirectory $sourceFile.Name
    }
}

$differences = @($mappings | Where-Object {
    (Get-ContentHash $_.Source) -ne (Get-ContentHash $_.Destination)
})

$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($mapping in $mappings) { [void] $expected.Add([IO.Path]::GetFullPath($mapping.Destination)) }

$staleFiles = @()
if (Test-Path -LiteralPath $destinationDirectory -PathType Container) {
    $staleFiles += @(Get-ChildItem -LiteralPath $destinationDirectory -Filter '*.sbn' -File |
        Where-Object { -not $expected.Contains($_.FullName) })
}

if ($Check) {
    if ($differences.Count -eq 0 -and $staleFiles.Count -eq 0) {
        Write-Host "Portal templates are byte-for-byte synchronized ($($mappings.Count) files)."
        return
    }
    foreach ($difference in $differences) {
        Write-Error "Out of sync: $($difference.Destination)" -ErrorAction Continue
    }
    foreach ($staleFile in $staleFiles) {
        Write-Error "Stale copied file: $($staleFile.FullName)" -ErrorAction Continue
    }
    throw 'The copied portal templates differ from the authoritative sibling repository. Run this script without -Check to synchronize it.'
}

New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
foreach ($mapping in $differences) {
    Copy-Item -LiteralPath $mapping.Source -Destination $mapping.Destination -Force
    Write-Host "Updated $($mapping.Destination)"
}
foreach ($staleFile in $staleFiles) {
    Remove-Item -LiteralPath $staleFile.FullName -Force
    Write-Host "Removed stale $($staleFile.FullName)"
}

Write-Host "Synchronized $($mappings.Count) portal template(s) from '$SourceRepository'."
