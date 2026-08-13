param(
    [Parameter(Mandatory = $true)][string] $Directory,
    [Parameter(Mandatory = $true)][string] $Version
)

$ErrorActionPreference = 'Stop'
$config = Get-Content (Join-Path $PSScriptRoot 'release.json') -Raw | ConvertFrom-Json

foreach ($id in $config.packageIds) {
    $package = Join-Path $Directory "$id.$Version.nupkg"
    $symbols = Join-Path $Directory "$id.$Version.snupkg"
    if (-not (Test-Path $package)) { throw "Missing package: $package" }
    if (-not (Test-Path $symbols)) { throw "Missing symbols package: $symbols" }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("semantic-knowledge-nupkg-" + [Guid]::NewGuid())
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($package, $temp)
        $nuspec = Get-ChildItem $temp -Filter '*.nuspec' | Select-Object -First 1
        if (-not $nuspec) { throw "$package does not contain a nuspec." }
        [xml]$xml = Get-Content $nuspec.FullName -Raw
        $metadata = $xml.package.metadata
        if ([string]$metadata.id -ne $id) { throw "Unexpected package id '$($metadata.id)' in $package." }
        if ([string]$metadata.version -ne $Version) { throw "Unexpected package version '$($metadata.version)' in $package." }
        if (-not $metadata.license) { throw "$package is missing license metadata." }
        if (-not $metadata.repository) { throw "$package is missing repository metadata." }
        if ([string]$metadata.readme -ne 'README.md') { throw "$package is missing README package metadata." }
        if ([string]$metadata.icon -ne '128x128_compressed.png') { throw "$package is missing package icon metadata." }
        if (-not (Test-Path (Join-Path $temp 'README.md'))) { throw "$package does not contain README.md." }
        if (-not (Test-Path (Join-Path $temp '128x128_compressed.png'))) { throw "$package does not contain the package icon." }
        $modelWeights = Get-ChildItem $temp -Recurse -File | Where-Object { $_.Extension -in '.onnx', '.safetensors', '.gguf', '.bin' -and $_.FullName -notmatch '[\\/]lib[\\/]' }
        if ($modelWeights) { throw "$package unexpectedly contains model-weight files." }
    }
    finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host "Validated SemanticKnowledge NuGet packages for version $Version."
