param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$TrainingRepo = "",
    [string]$Checkpoint = "",
    [switch]$AutoVersion,
    [switch]$NoRestore,
    [switch]$UseBundledModel
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$frontend = Join-Path $root "frontend"
$project = Join-Path $root "src\DeleteImageGrids\DeleteImageGrids.csproj"
$testProject = Join-Path $root "tests\DeleteImageGrids.RegressionTests\DeleteImageGrids.RegressionTests.csproj"
$model = Join-Path $root "src\DeleteImageGrids\model\image-grid-classifier.onnx"
$artifacts = Join-Path $root "artifacts"
$standardPublish = Join-Path $artifacts "extension-standard"
$developerPublish = Join-Path $artifacts "extension-dev"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content (Join-Path $root "src\DeleteImageGrids\extension.json") -Raw | ConvertFrom-Json).version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be a three-part release version." }

if (-not $UseBundledModel) {
if ([string]::IsNullOrWhiteSpace($TrainingRepo)) {
    $TrainingRepo = [IO.Path]::GetFullPath((Join-Path $root "..\..\gitea\img_grid_classifier"))
} else { $TrainingRepo = [IO.Path]::GetFullPath($TrainingRepo) }
$python = Join-Path $TrainingRepo ".venv\Scripts\python.exe"
$checkpoint = if ([string]::IsNullOrWhiteSpace($Checkpoint)) { Join-Path $TrainingRepo "model.pt" } else { [IO.Path]::GetFullPath($Checkpoint) }
if (-not (Test-Path -LiteralPath $python)) { throw "Training repository Python is missing: $python" }
if (-not (Test-Path -LiteralPath $checkpoint)) { throw "Training checkpoint is missing: $checkpoint" }

& $python -m unittest discover -s $TrainingRepo -p "test_*.py"
if ($LASTEXITCODE -ne 0) { throw "Training policy tests failed with exit code $LASTEXITCODE." }
}

if ($AutoVersion) {
    $sequencePath = Join-Path $artifacts "build-sequence.txt"
    $sequence = 0
    if (Test-Path -LiteralPath $sequencePath) {
        $parsed = 0
        if ([int]::TryParse((Get-Content -LiteralPath $sequencePath -Raw).Trim(), [ref]$parsed)) { $sequence = $parsed }
    }
    $sequence = [Math]::Max(0, $sequence) + 1
    $Version = "0.2.$sequence"
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    Set-Content -LiteralPath $sequencePath -Value $sequence -Encoding ASCII
}
elseif ($Version -match '^0\.2\.(\d+)$') {
    $sequencePath = Join-Path $artifacts "build-sequence.txt"
    $existing = 0
    if (Test-Path -LiteralPath $sequencePath) {
        $parsed = 0
        if ([int]::TryParse((Get-Content -LiteralPath $sequencePath -Raw).Trim(), [ref]$parsed)) { $existing = $parsed }
    }
    $declared = [int]$Matches[1]
    if ($declared -gt $existing) {
        New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
        Set-Content -LiteralPath $sequencePath -Value $declared -Encoding ASCII
    }
}

if (-not $UseBundledModel) {
    & $python (Join-Path $root "scripts\export-model.py") $checkpoint $model --verify-dir (Join-Path $TrainingRepo "data") --verify-limit 64
    if ($LASTEXITCODE -ne 0) { throw "Model export failed with exit code $LASTEXITCODE." }
} elseif (-not (Test-Path -LiteralPath $model) -or -not (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($model, ".preprocess")))) {
    throw "Bundled model and preprocessing metadata are required."
}

if (-not $env:COVE_HOST_ASSEMBLY_DIR) {
    $cove = Get-Process Cove -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cove) {
        $core = $cove.Modules | Where-Object ModuleName -eq "Cove.Core.dll" | Select-Object -First 1
        if ($core) { $env:COVE_HOST_ASSEMBLY_DIR = Split-Path -Parent $core.FileName }
    }
}
if (-not $env:COVE_HOST_ASSEMBLY_DIR) { throw "Cove 1.1.0 must be running, or COVE_HOST_ASSEMBLY_DIR must point to its assemblies." }
$hostArg = "-p:CoveHostAssemblyDir=$env:COVE_HOST_ASSEMBLY_DIR"

Push-Location $frontend
try {
    npm test
    if ($LASTEXITCODE -ne 0) { throw "Frontend tests failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

if (-not $NoRestore) {
    dotnet restore $project -p:UseLocalCovePlugins=false $hostArg
    if ($LASTEXITCODE -ne 0) { throw "Extension restore failed with exit code $LASTEXITCODE." }
    dotnet restore $testProject
    if ($LASTEXITCODE -ne 0) { throw "Regression-test restore failed with exit code $LASTEXITCODE." }
}

function Set-PublishedManifest([string]$PublishDirectory, [bool]$Developer) {
    $path = Join-Path $PublishDirectory "extension.json"
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $manifest.version = $Version
    if ($Developer) {
        $manifest.name = "Delete Image Grids (Developer Edition)"
        $manifest.description = "Developer edition with dataset feedback, self-training, and dual artifact builds."
    }
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding UTF8
}

function Build-Variant([bool]$Developer, [string]$PublishDirectory) {
    $resolvedPublish = [IO.Path]::GetFullPath($PublishDirectory)
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'
    if (-not $resolvedPublish.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish directory must be inside the artifacts directory."
    }
    if (Test-Path -LiteralPath $PublishDirectory) { Remove-Item -LiteralPath $PublishDirectory -Recurse -Force }
    Push-Location $frontend
    try {
        $env:IGD_DEV_EDITION = if ($Developer) { "true" } else { "false" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "Frontend build failed with exit code $LASTEXITCODE." }
    }
    finally { Remove-Item Env:IGD_DEV_EDITION -ErrorAction SilentlyContinue; Pop-Location }

    $developerValue = if ($Developer) { "true" } else { "false" }
    dotnet publish $project -c $Configuration -o $PublishDirectory -p:UseLocalCovePlugins=false `
        $hostArg "-p:DeveloperEdition=$developerValue" "-p:ExtensionVersion=$Version" --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Extension publish failed with exit code $LASTEXITCODE." }
    Set-PublishedManifest $PublishDirectory $Developer
}

dotnet run --project $testProject -c $Configuration -p:DeveloperEdition=true --no-restore
if ($LASTEXITCODE -ne 0) { throw "Regression tests failed with exit code $LASTEXITCODE." }

Build-Variant $false $standardPublish
Build-Variant $true $developerPublish

$normalZip = Join-Path $artifacts "io.github.jiwenjimiran.delete-image-grids-$Version.zip"
$developerZip = Join-Path $artifacts "io.github.jiwenjimiran.delete-image-grids-$($Version)_dev.zip"
foreach ($zip in @($normalZip, $developerZip)) { if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force } }
Compress-Archive -Path (Join-Path $standardPublish "*") -DestinationPath $normalZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $developerPublish "*") -DestinationPath $developerZip -CompressionLevel Optimal

$receipt = [ordered]@{
    version = $Version
    normalArtifact = $normalZip
    developerArtifact = $developerZip
    developerPublishDirectory = $developerPublish
    builtAt = (Get-Date).ToString("o")
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts "latest-build.json") -Encoding UTF8
Write-Output $normalZip
Write-Output $developerZip
