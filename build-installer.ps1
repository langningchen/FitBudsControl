$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'src\FitBudsControl\FitBudsControl.csproj'
$InstallerScript = Join-Path $Root 'installer\FitBudsControl.iss'

& (Join-Path $Root 'publish.ps1')

[xml]$ProjectXml = Get-Content $Project
$Version = [string]($ProjectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Unable to read Version from FitBudsControl.csproj.'
}

$Candidates = @(
    (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$Iscc = $Candidates | Select-Object -First 1
if (-not $Iscc) {
    throw 'Inno Setup Compiler (ISCC.exe) was not found. Install Inno Setup 6.5+ or 7.x first.'
}

$LanguageFile = Join-Path $Root 'installer\ChineseSimplified.isl'
if (-not (Test-Path $LanguageFile)) {
    $LanguageUrl = 'https://raw.githubusercontent.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/main/ChineseSimplified.isl'
    Write-Host 'Downloading Simplified Chinese messages for Inno Setup...'
    try {
        Invoke-WebRequest -Uri $LanguageUrl -OutFile $LanguageFile -UseBasicParsing
    }
    catch {
        $DefaultMessages = Join-Path (Split-Path -Parent $Iscc) 'Default.isl'
        if (-not (Test-Path $DefaultMessages)) {
            throw 'Unable to download Chinese installer messages and Default.isl was not found.'
        }
        Write-Warning 'Chinese installer messages could not be downloaded; falling back to the default Inno Setup language.'
        Copy-Item $DefaultMessages $LanguageFile -Force
    }
}

$InstallerOutput = Join-Path $Root 'artifacts\installer'
Remove-Item $InstallerOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $InstallerOutput | Out-Null

Write-Host "Building installer with Inno Setup: $Iscc"
& $Iscc "/DMyAppVersion=$Version" $InstallerScript
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE."
}

$Installer = Join-Path $InstallerOutput "FitBudsControl-Setup-$Version.exe"
if (-not (Test-Path $Installer)) {
    throw "Installer build succeeded but output was not found: $Installer"
}

Write-Host ''
Write-Host 'Multi-file installer build complete:'
Write-Host "  $Installer"
