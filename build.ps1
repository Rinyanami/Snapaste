param([switch]$Portable)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = if (Test-Path '.tools\dotnet\dotnet.exe') { (Resolve-Path '.tools\dotnet\dotnet.exe').Path } else { (Get-Command dotnet -ErrorAction Stop).Source }
& (Join-Path $PSScriptRoot 'scripts/Get-OcrModels.ps1')
if (-not $?) { throw 'OCR model preparation failed' }
& $dotnet restore Snapaste.sln
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
& $dotnet build Snapaste.sln -c Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if ($Portable) {
    $projectVersion = ([xml](Get-Content -LiteralPath 'src/Snapaste.App/Snapaste.App.csproj' -Raw)).Project.PropertyGroup.Version
    if (Test-Path 'artifacts/Snapaste-portable') { Remove-Item -LiteralPath 'artifacts/Snapaste-portable' -Recurse -Force }
    & $dotnet publish src/Snapaste.App/Snapaste.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o artifacts/Snapaste-portable
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md artifacts/Snapaste-portable/README.md -Force
    Copy-Item VALIDATION.md artifacts/Snapaste-portable/VALIDATION.md -Force
    Compress-Archive -LiteralPath artifacts/Snapaste-portable -DestinationPath "artifacts/Snapaste-$projectVersion-win-x64.zip" -Force
}
