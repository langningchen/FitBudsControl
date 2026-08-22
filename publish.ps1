$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'src\FitBudsControl\FitBudsControl.csproj'
$OutputDir = Join-Path $Root 'artifacts\single-file'

Write-Host 'Cleaning single-file publish output...'
Remove-Item $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host 'Publishing FitBudsControl as a self-contained single EXE...'
$PublishArgs = @(
    'publish',
    $Project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:Platform=x64',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:EnableMsixTooling=true',
    '-p:PublishSingleFile=true',
    '-p:IncludeAllContentForSelfExtract=true',
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

Write-Host ''
Write-Host 'Single-file publish complete:'
Write-Host "  $Exe"
