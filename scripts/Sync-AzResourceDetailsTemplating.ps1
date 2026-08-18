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

$sourceRoots = @(
    [pscustomobject]@{
        Source = Join-Path $SourceRepository 'src\AzResourceDetails.Templating'
        Destination = Join-Path $repositoryRoot 'src\AzResourceDetails.Templating'
    },
    [pscustomobject]@{
        Source = Join-Path $SourceRepository 'tests\AzResourceDetails.Templating.Tests'
        Destination = Join-Path $repositoryRoot 'tests\AzResourceDetails.Templating.Tests'
    }
)
$sourceReferenceData = Join-Path $SourceRepository 'config\azure-locations.json'
$destinationReferenceData = Join-Path $repositoryRoot 'config\azure-locations.json'

foreach ($root in $sourceRoots) {
    if (-not (Test-Path -LiteralPath $root.Source -PathType Container)) {
        throw "Required shared-runtime directory not found at '$($root.Source)'. Pass -SourceRepository explicitly if the repositories are not siblings."
    }
    if ([IO.Path]::GetFullPath($root.Source).Equals([IO.Path]::GetFullPath($root.Destination), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source and destination directories must be different.'
    }
}
if (-not (Test-Path -LiteralPath $sourceReferenceData -PathType Leaf)) {
    throw "Required region reference data not found at '$sourceReferenceData'."
}

function Get-ContentHash([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$mappings = @()
foreach ($root in $sourceRoots) {
    $sourceFiles = @(Get-ChildItem -LiteralPath $root.Source -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Sort-Object FullName)
    if ($sourceFiles.Count -eq 0) {
        throw "No source files found in '$($root.Source)'."
    }
    foreach ($sourceFile in $sourceFiles) {
        $relativePath = [IO.Path]::GetRelativePath($root.Source, $sourceFile.FullName)
        $mappings += [pscustomobject]@{
            Source = $sourceFile.FullName
            Destination = Join-Path $root.Destination $relativePath
            DestinationRoot = $root.Destination
        }
    }
}
$mappings += [pscustomobject]@{
    Source = $sourceReferenceData
    Destination = $destinationReferenceData
    DestinationRoot = Join-Path $repositoryRoot 'config'
}

$differences = @($mappings | Where-Object {
    (Get-ContentHash $_.Source) -ne (Get-ContentHash $_.Destination)
})

$staleFiles = @()
foreach ($root in $sourceRoots) {
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($mapping in $mappings | Where-Object { $_.DestinationRoot -eq $root.Destination }) {
        [void] $expected.Add([IO.Path]::GetFullPath($mapping.Destination))
    }
    if (Test-Path -LiteralPath $root.Destination -PathType Container) {
        $staleFiles += @(Get-ChildItem -LiteralPath $root.Destination -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and -not $expected.Contains($_.FullName) })
    }
}

if ($Check) {
    if ($differences.Count -eq 0 -and $staleFiles.Count -eq 0) {
        Write-Host 'AzResourceDetails.Templating projects and reference data are byte-for-byte synchronized.'
        return
    }
    foreach ($difference in $differences) {
        Write-Error "Out of sync: $($difference.Destination)" -ErrorAction Continue
    }
    foreach ($staleFile in $staleFiles) {
        Write-Error "Stale copied file: $($staleFile.FullName)" -ErrorAction Continue
    }
    throw 'The copied shared runtime differs from the authoritative sibling repository. Run this script without -Check to synchronize it.'
}

foreach ($mapping in $differences) {
    $destinationDirectory = Split-Path $mapping.Destination -Parent
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $mapping.Source -Destination $mapping.Destination -Force
    Write-Host "Updated $($mapping.Destination)"
}
foreach ($staleFile in $staleFiles) {
    Remove-Item -LiteralPath $staleFile.FullName -Force
    Write-Host "Removed stale $($staleFile.FullName)"
}

Write-Host "Synchronized the complete shared runtime and test projects from '$SourceRepository'."