param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin\WEAM.Revit.AI.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -ne '0.9.9') { throw 'Update installer version constants before building a new release.' }
& (Join-Path $PSScriptRoot 'build.ps1')
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js LTS is required on the build computer.' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'server\node_modules\zod'))) {
    Push-Location (Join-Path $PSScriptRoot 'server')
    try { pnpm install --frozen-lockfile; if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' } }
    finally { Pop-Location }
}
$stageRoot = Join-Path $PSScriptRoot ('installer\obj\package-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stageRoot 'payload'
$source = Join-Path $stageRoot 'source'
New-Item -ItemType Directory -Force -Path $payload,$source,$OutputDirectory | Out-Null
node (Join-Path $PSScriptRoot 'installer\package.mjs') $PSScriptRoot $payload
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
# Use Git's tracked + nonignored files; never include runtime caches or private models.
Push-Location $PSScriptRoot
try { $sourceFiles = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard) }
finally { Pop-Location }
foreach ($relative in $sourceFiles) {
    if ($relative -match '(^|[\\/])(bin|obj|node_modules|dist|\.git)([\\/]|$)' -or $relative -match '\.(rvt|rfa|rte|rft|token|zip|exe|pdb)$') { continue }
    $inputFile = Join-Path $PSScriptRoot $relative
    if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) { continue }
    $destination = Join-Path $source $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $inputFile -Destination $destination
}
$sourceArchive = Join-Path $OutputDirectory "WEAM.Revit.AI-$version-source.zip"
Compress-Archive -Path (Join-Path $source '*') -DestinationPath $sourceArchive -Force
New-Item -ItemType Directory -Path (Join-Path $payload 'source') -Force | Out-Null
Copy-Item -LiteralPath $sourceArchive -Destination (Join-Path $payload 'source')
$payloadArchive = Join-Path $stageRoot 'payload.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadArchive -CompressionLevel Optimal
dotnet publish (Join-Path $PSScriptRoot 'installer\WEAM.Revit.AI.Setup.csproj') -c Release -o (Join-Path $stageRoot 'publish') "-p:PayloadPath=$payloadArchive" --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$setup = Join-Path $OutputDirectory "WEAM.Revit.AI-$version-Setup.exe"
Copy-Item -LiteralPath (Join-Path $stageRoot 'publish\WEAM.Revit.AI.Setup.exe') -Destination $setup -Force
$hashLines = foreach ($file in $setup,$sourceArchive) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $hashLines)
Write-Host "Installer: $setup"
Write-Host "Sources:   $sourceArchive"
