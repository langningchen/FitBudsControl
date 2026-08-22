$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'src\FitBudsControl\FitBudsControl.csproj'
$ArtifactsDir = Join-Path $Root 'artifacts'
$OutputDir = Join-Path $ArtifactsDir 'portable\FitBudsControl'
$ProjectXml = [xml](Get-Content $Project)
$Version = [string]($ProjectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Unable to read Version from FitBudsControl.csproj.'
}
$ZipPath = Join-Path $ArtifactsDir "portable\FitBudsControl-Portable-$Version.zip"

Write-Host 'Cleaning multi-file portable publish output...'
Remove-Item $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host 'Publishing FitBudsControl as a self-contained multi-file application...'
$PublishArgs = @(
    'publish',
    $Project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:Platform=x64',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:EnableMsixTooling=true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $OutputDir
)

& dotnet @PublishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$Exe = Join-Path $OutputDir 'FitBudsControl.exe'
if (-not (Test-Path $Exe)) {
    throw "dotnet publish succeeded but FitBudsControl.exe was not found: $Exe"
}

Write-Host "Creating portable archive: $ZipPath"
Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal

Write-Host ''
Write-Host 'Portable publish complete:'
Write-Host "  Directory: $OutputDir"
Write-Host "  Archive:   $ZipPath"
