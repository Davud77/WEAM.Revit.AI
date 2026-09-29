param(
    [string]$Revit2026Dir = 'C:\Program Files\Autodesk\Revit 2026'
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'plugin\WEAM.Revit.AI.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Не найден dotnet. Установите .NET 8 SDK, затем повторите запуск.'
}
if (-not (Test-Path (Join-Path $Revit2026Dir 'RevitAPI.dll'))) {
    throw "Не найден RevitAPI.dll в '$Revit2026Dir'. Укажите каталог Revit 2026 через параметр -Revit2026Dir."
}

$sdkEntry = @(dotnet --list-sdks) | Where-Object { $_ -match '^\s*\d+\.\d+\.\d+\s+\[' } | Select-Object -Last 1
if ($sdkEntry -notmatch '^\s*(\d+\.\d+\.\d+)\s+\[(.+)\]\s*$') {
    throw 'Не удалось определить каталог .NET SDK через dotnet --list-sdks.'
}
$sdkVersion = $Matches[1]
$sdkRoot = $Matches[2]
$targetingPacks = Join-Path (Split-Path $sdkRoot -Parent) 'packs\Microsoft.NETCore.App.Ref'

if (Test-Path $targetingPacks) {
    dotnet build $projectPath --configuration Release --verbosity quiet "-p:Revit2026Dir=$Revit2026Dir"
    if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с кодом $LASTEXITCODE." }
    return
}

Write-Warning 'В установленном SDK отсутствует пакет ссылочных сборок .NET 8. Использую локальный компилятор и установленную среду выполнения .NET 8.'
$compiler = Join-Path $sdkRoot "$sdkVersion\Roslyn\bincore\csc.dll"
if (-not (Test-Path $compiler)) { throw "Не найден компилятор C#: $compiler" }

$runtimeEntry = @(dotnet --list-runtimes) | Where-Object { $_ -match '^Microsoft\.NETCore\.App\s+8\.' } | Select-Object -Last 1
if ($runtimeEntry -notmatch '^Microsoft\.NETCore\.App\s+(\d+\.\d+\.\d+)\s+\[(.+)\]\s*$') {
    throw 'Не найдена установленная среда выполнения Microsoft.NETCore.App 8.x.'
}
$runtimeDirectory = Join-Path $Matches[2] $Matches[1]
if (-not (Test-Path $runtimeDirectory)) { throw "Не найдена папка среды выполнения: $runtimeDirectory" }
$desktopEntry = @(dotnet --list-runtimes) | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App\s+8\.' } | Select-Object -Last 1
if ($desktopEntry -notmatch '^Microsoft\.WindowsDesktop\.App\s+(\d+\.\d+\.\d+)\s+\[(.+)\]\s*$') {
    throw 'Для значков Revit требуется установленная среда Microsoft.WindowsDesktop.App 8.x.'
}
$desktopDirectory = Join-Path $Matches[2] $Matches[1]

$pluginDirectory = Join-Path $PSScriptRoot 'plugin'
$outputDirectory = Join-Path $pluginDirectory 'bin\Release\net8.0-windows'
$outputAssembly = Join-Path $outputDirectory 'WEAM.Revit.AI.dll'
$runtimeReferences = foreach ($file in Get-ChildItem -LiteralPath $runtimeDirectory -Filter '*.dll') {
    try {
        [void][System.Reflection.AssemblyName]::GetAssemblyName($file.FullName)
        "-reference:$($file.FullName)"
    } catch { }
}
$sourceFiles = @(Get-ChildItem -LiteralPath $pluginDirectory -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
    Select-Object -ExpandProperty FullName)
$fallbackFiles = @(
    (Join-Path $PSScriptRoot 'build-fallback\GlobalUsings.cs'),
    (Join-Path $PSScriptRoot 'build-fallback\AssemblyInfo.cs'),
    (Join-Path $PSScriptRoot 'build-fallback\TargetFrameworkAttributes.cs')
)
if (@($fallbackFiles | Where-Object { -not (Test-Path $_) }).Count -gt 0) {
    throw 'Не найдены вспомогательные файлы build-fallback.'
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$compilerArguments = @(
    '-nostdlib+', '-target:library', '-langversion:latest', '-nullable:enable', '-optimize+',
    '-define:TRACE,RELEASE', "-out:$outputAssembly",
    "-reference:$(Join-Path $Revit2026Dir 'RevitAPI.dll')",
    "-reference:$(Join-Path $Revit2026Dir 'RevitAPIUI.dll')"
    "-reference:$(Join-Path $desktopDirectory 'PresentationCore.dll')"
    "-reference:$(Join-Path $desktopDirectory 'WindowsBase.dll')"
    "-resource:$(Join-Path $pluginDirectory 'Assets\weam-ai.png'),WEAM.Revit.AI.Assets.weam-ai.png"
) + @($runtimeReferences) + $fallbackFiles + $sourceFiles
$responsePath = Join-Path ([System.IO.Path]::GetTempPath()) 'WEAM.Revit.AI-csc-fallback.rsp'
$responseLines = $compilerArguments | ForEach-Object {
    if ($_ -match '\s|,') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
}
[System.IO.File]::WriteAllLines($responsePath, $responseLines, [System.Text.UTF8Encoding]::new($false))
dotnet $compiler "@$responsePath"
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с кодом $LASTEXITCODE." }
Write-Host "Сборка завершена: $outputAssembly"
