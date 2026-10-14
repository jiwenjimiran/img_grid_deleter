param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][int]$CoveProcessId,
    [Parameter(Mandatory = $true)][string]$Receipt
)

$ErrorActionPreference = "Stop"
$expectedId = "io.github.jiwenjimiran.delete-image-grids"
$sourcePath = [IO.Path]::GetFullPath($Source)
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ((Split-Path -Leaf $destinationPath) -ne $expectedId) { throw "Refusing to update unexpected destination: $destinationPath" }
$manifestPath = Join-Path $sourcePath "extension.json"
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Staged extension manifest is missing." }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.id -ne $expectedId) { throw "Staged extension id is invalid." }

while (Get-Process -Id $CoveProcessId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }

$parent = Split-Path -Parent $destinationPath
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$incoming = Join-Path $parent ".$expectedId-update-$stamp"
$backup = Join-Path $parent "$expectedId.backup-$stamp"
try {
    New-Item -ItemType Directory -Path $incoming | Out-Null
    Copy-Item -Path (Join-Path $sourcePath "*") -Destination $incoming -Recurse -Force
    if (Test-Path -LiteralPath $destinationPath) { Move-Item -LiteralPath $destinationPath -Destination $backup }
    Move-Item -LiteralPath $incoming -Destination $destinationPath
    @{ applied = $true; version = $manifest.version; appliedAt = (Get-Date).ToString("o"); backup = $backup } |
        ConvertTo-Json | Set-Content -LiteralPath $Receipt -Encoding UTF8
}
catch {
    @{ applied = $false; error = $_.Exception.Message; failedAt = (Get-Date).ToString("o") } |
        ConvertTo-Json | Set-Content -LiteralPath $Receipt -Encoding UTF8
    throw
}
