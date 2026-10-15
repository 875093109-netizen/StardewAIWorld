param(
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '../src/AIWorld.Demo/bin/Release/net6.0'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../releases')
)
$ErrorActionPreference = 'Stop'
$files = @('AIWorld.Core.dll', 'AIWorld.Adapters.dll', 'AIWorld.Demo.dll',
    'AIWorld.Demo.deps.json', 'manifest.json', 'assets/story-archive.json',
    'assets/schedule-activities.json', 'assets/job-rules.json')
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $BuildDirectory $file) -PathType Leaf)) {
        throw "Missing build file: $file"
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $BuildDirectory 'manifest.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputPath = Join-Path $OutputDirectory "AIWorld-$($manifest.Version)-alpha.zip"
if (Test-Path -LiteralPath $outputPath) { throw "Package already exists: $outputPath" }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($outputPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
            (Join-Path $BuildDirectory $file), "AIWorld.Demo/$file",
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([System.IO.Path]::GetFileName($outputPath))" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Created $outputPath ($hash)"
