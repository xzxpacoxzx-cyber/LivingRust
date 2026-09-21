param(
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [Parameter(Mandatory = $true)][string]$OutputZip
)

$ErrorActionPreference = "Stop"

$projectDir = (Resolve-Path $ProjectDir).Path.TrimEnd('\')

$sourceFiles = Get-ChildItem -Path $projectDir -Recurse -Filter "*.cs" |
    Where-Object {
        $_.FullName -notmatch '\\obj\\' -and
        $_.FullName -notmatch '\\bin\\'
    }

if ($sourceFiles.Count -eq 0) {
    throw "No .cs source files found under $projectDir"
}

$duplicates = $sourceFiles | Group-Object Name | Where-Object { $_.Count -gt 1 }
if ($duplicates) {
    $names = ($duplicates | ForEach-Object { $_.Name }) -join ', '
    throw "Duplicate filenames found ($names) - a flat .cszip package cannot contain two files with the same name."
}

$outputDir = Split-Path -Parent $OutputZip
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

if (Test-Path $OutputZip) {
    Remove-Item $OutputZip -Force
}

# Compress-Archive rejects non-.zip extensions (e.g. Carbon's .cszip), so build as .zip
# in a temp location first, then move it into place under the real name.
$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) "$([System.IO.Path]::GetFileNameWithoutExtension($OutputZip))-$([guid]::NewGuid()).zip"

try {
    Compress-Archive -Path $sourceFiles.FullName -DestinationPath $tempZip -CompressionLevel Optimal
    Move-Item -Path $tempZip -Destination $OutputZip -Force
}
finally {
    if (Test-Path $tempZip) {
        Remove-Item $tempZip -Force
    }
}

Write-Host "Packaged $($sourceFiles.Count) source files into $OutputZip"
