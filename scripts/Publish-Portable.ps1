[CmdletBinding()]
param(
    [string]$Tag,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Framework = "net8.0-windows10.0.19041.0"
)

$ErrorActionPreference = "Stop"

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed:`n$output"
    }

    return $output
}

function Invoke-GitOrNull {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $output = & git @Arguments 2>&1
    $ErrorActionPreference = $previousErrorActionPreference
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return $output
}

$gitRootOutput = Invoke-GitOrNull -Arguments @("-C", $PSScriptRoot, "rev-parse", "--show-toplevel")
if ($gitRootOutput) {
    $repoRoot = ($gitRootOutput | Select-Object -First 1).Trim()
} else {
    $repoRoot = Split-Path -Parent $PSScriptRoot
}

$projectPath = Join-Path $repoRoot "AppleMusicTranslator.csproj"

$semVerTagPattern = "^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"
$headCommitOutput = Invoke-GitOrNull -Arguments @("-C", $repoRoot, "rev-parse", "HEAD")
$hasHeadCommit = $null -ne $headCommitOutput
$headCommit = if ($hasHeadCommit) { ($headCommitOutput | Select-Object -First 1).Trim() } else { "no-commit" }

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $releaseName = "dev-" + (Get-Date -Format "yyyyMMdd-HHmmss")
    $shortCommit = if ($hasHeadCommit) {
        (Invoke-Git -Arguments @("-C", $repoRoot, "rev-parse", "--short=12", "HEAD") | Select-Object -First 1).Trim()
    } else {
        "no-commit"
    }
} else {
    if (-not $hasHeadCommit) {
        throw "Cannot create a tagged release before the repository has a commit. Commit the source first, or run the script without -Tag for a temporary portable build."
    }

    $status = & git -C $repoRoot status --porcelain
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed"
    }

    if ($status) {
        throw "Working tree is not clean. Commit or stash changes before creating a tagged release build."
    }

    if ($Tag -notmatch $semVerTagPattern) {
        throw "Release tag '$Tag' is not a supported SemVer tag. Use a tag like v0.2.0-beta.1."
    }

    $tagCommit = (Invoke-Git -Arguments @("-C", $repoRoot, "rev-list", "-n", "1", $Tag) | Select-Object -First 1).Trim()
    if ($tagCommit -ne $headCommit) {
        throw "Release tag '$Tag' does not point at HEAD. Check out the tagged commit before publishing."
    }

    $releaseName = $Tag
    $shortCommit = (Invoke-Git -Arguments @("-C", $repoRoot, "rev-parse", "--short=12", "HEAD") | Select-Object -First 1).Trim()
}

$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "Release"))
$releaseDir = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot $releaseName))

if (-not $releaseDir.StartsWith($releaseRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Resolved release directory is outside the Release root."
}

$publishDir = Join-Path $releaseDir "portable-$Runtime"
$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("AppleMusicTranslator-build-" + [Guid]::NewGuid().ToString("N"))
$buildObjDir = Join-Path $buildRoot "obj"
$buildBinDir = Join-Path $buildRoot "bin"
$exePath = Join-Path $publishDir "AppleMusicTranslator.exe"
$readmePath = Join-Path $publishDir "README.txt"
$zipPath = Join-Path $releaseDir "AppleMusicTranslator-$releaseName-$Runtime-portable.zip"
$hashPath = Join-Path $releaseDir "SHA256SUMS.txt"

if (Test-Path -LiteralPath $releaseDir) {
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
}

if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=true `
    /p:PublishReadyToRun=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:BaseIntermediateOutputPath="$buildObjDir\" `
    /p:BaseOutputPath="$buildBinDir\"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

$releaseReadme = @(
    "AppleMusic Translator Portable Build",
    "Build: $releaseName",
    "Commit: $shortCommit",
    "",
    "This is a self-contained single-file build. No .NET install is required.",
    "Double-click AppleMusicTranslator.exe to run.",
    "",
    "Windows SmartScreen may show a warning because this build is not code-signed.",
    "If you trust the source, click More info, then Run anyway.",
    "",
    "If an older version is already running, quit it from the tray menu first.",
    "",
    "System requirements:",
    "- Windows 10 2004 / build 19041 or newer",
    "- Windows 11",
    "- 64-bit Windows"
) -join [Environment]::NewLine

Set-Content -LiteralPath $readmePath -Value $releaseReadme -Encoding UTF8

Compress-Archive `
    -Path $exePath, $readmePath `
    -DestinationPath $zipPath `
    -CompressionLevel Optimal

$exe = Get-Item -LiteralPath $exePath
$zip = Get-Item -LiteralPath $zipPath
$hashLines = foreach ($artifact in @($exe.FullName, $zip.FullName)) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $artifact
    $relativePath = $artifact.Substring($releaseDir.Length).TrimStart("\", "/")
    "$($hash.Hash)  $relativePath"
}

Set-Content -LiteralPath $hashPath -Value ($hashLines -join [Environment]::NewLine) -Encoding ASCII

Write-Host ""
Write-Host "Portable build is ready:"
Write-Host "  Build: $releaseName"
Write-Host "  Commit: $shortCommit"
Write-Host "  EXE: $($exe.FullName)"
Write-Host "  EXE size: $([Math]::Round($exe.Length / 1MB, 2)) MB"
Write-Host "  ZIP: $($zip.FullName)"
Write-Host "  ZIP size: $([Math]::Round($zip.Length / 1MB, 2)) MB"
Write-Host "  SHA256SUMS: $hashPath"
Write-Host ""
Write-Host "For GitHub Release, upload the ZIP first. Do not upload framework-dependent small exe files alone."
