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

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd($trimChars)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use $Description outside the expected root. Path: $fullPath Root: $fullRoot"
    }

    return $fullPath
}

function Remove-SafeDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $fullPath = Assert-ChildPath -Path $Path -Root $AllowedRoot -Description $Description
    Write-Host "Clean ${Description}: $fullPath"

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Invoke-DotNetBuildServerShutdown {
    Write-Host "Shut down dotnet build servers"
    $output = & dotnet build-server shutdown 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build-server shutdown failed with exit code $LASTEXITCODE`n$output"
    }

    if ($output) {
        $output | ForEach-Object { Write-Host "  $_" }
    }
}

function Get-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not created: $Path"
    }

    return Get-Item -LiteralPath $Path
}

function Test-PortableZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $zip = Get-RequiredFile -Path $Path -Description "Portable ZIP"
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try {
        $entryNames = @(
            $archive.Entries |
                Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
                ForEach-Object { $_.FullName.Replace("\", "/") }
        )
        $requiredEntries = @("AppleMusicTranslator.exe", "README.txt")
        $requiredLookup = @{}

        foreach ($requiredEntry in $requiredEntries) {
            $requiredLookup[$requiredEntry.ToLowerInvariant()] = $true
            if (-not ($entryNames | Where-Object { $_ -ieq $requiredEntry })) {
                throw "Portable ZIP is missing required file: $requiredEntry"
            }
        }

        $forbiddenEntries = @($entryNames | Where-Object {
            $_ -match "(?i)(\.dll$|\.pdb$|\.json$|\.deps$|\.runtimeconfig$)"
        })

        if ($forbiddenEntries.Count -gt 0) {
            $formattedEntries = ($forbiddenEntries | ForEach-Object { "  $_" }) -join [Environment]::NewLine
            throw "Portable ZIP contains loose framework/debug/config files:`n$formattedEntries"
        }

        $unexpectedEntries = @($entryNames | Where-Object {
            -not $requiredLookup.ContainsKey($_.ToLowerInvariant())
        })

        if ($unexpectedEntries.Count -gt 0) {
            $formattedEntries = ($unexpectedEntries | ForEach-Object { "  $_" }) -join [Environment]::NewLine
            throw "Portable ZIP contains unexpected files:`n$formattedEntries"
        }

        return $entryNames
    } finally {
        $archive.Dispose()
    }
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
$exePath = Join-Path $publishDir "AppleMusicTranslator.exe"
$readmePath = Join-Path $publishDir "README.txt"
$zipPath = Join-Path $releaseDir "AppleMusicTranslator-$releaseName-$Runtime-portable.zip"
$hashPath = Join-Path $releaseDir "SHA256SUMS.txt"
$repoBinRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "bin"))
$repoObjRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "obj"))
$repoBinConfigurationRoot = Assert-ChildPath -Path (Join-Path $repoBinRoot $Configuration) -Root $repoBinRoot -Description "bin/$Configuration root"
$repoObjConfigurationRoot = Assert-ChildPath -Path (Join-Path $repoObjRoot $Configuration) -Root $repoObjRoot -Description "obj/$Configuration root"

Write-Host ""
Write-Host "Prepare portable build:"
Write-Host "  Build: $releaseName"
Write-Host "  Commit: $shortCommit"
Write-Host "  Configuration: $Configuration"
Write-Host "  Runtime: $Runtime"
Write-Host "  Framework: $Framework"
Write-Host "  Release directory: $releaseDir"
Write-Host "  Publish directory: $publishDir"
Write-Host ""

Invoke-DotNetBuildServerShutdown
Remove-SafeDirectory -Path $repoBinConfigurationRoot -AllowedRoot $repoBinRoot -Description "repo bin configuration output"
Remove-SafeDirectory -Path $repoObjConfigurationRoot -AllowedRoot $repoObjRoot -Description "repo obj configuration output"
Remove-SafeDirectory -Path $releaseDir -AllowedRoot $releaseRoot -Description "release output"

$publishExitCode = 0
try {
    dotnet publish $projectPath `
        -c $Configuration `
        -f $Framework `
        -r $Runtime `
        --self-contained true `
        -o $publishDir `
        /p:PublishSingleFile=true `
        /p:PublishReadyToRun=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:DebugType=None `
        /p:DebugSymbols=false

    $publishExitCode = $LASTEXITCODE
} finally {
    Invoke-DotNetBuildServerShutdown
}

if ($publishExitCode -ne 0) {
    throw "dotnet publish failed with exit code $publishExitCode"
}

$exe = Get-RequiredFile -Path $exePath -Description "Published EXE"

$releaseReadme = @(
    "AppleMusic Translator Portable Build",
    "Build: $releaseName",
    "Commit: $shortCommit",
    "",
    "This is a portable ZIP package, not an installer.",
    "It contains a self-contained single-file build. No .NET install is required.",
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
$readme = Get-RequiredFile -Path $readmePath -Description "Portable README"

Compress-Archive `
    -Path $exe.FullName, $readme.FullName `
    -DestinationPath $zipPath `
    -CompressionLevel Optimal

$zipEntries = Test-PortableZip -Path $zipPath
$zip = Get-RequiredFile -Path $zipPath -Description "Portable ZIP"
$artifactHashes = foreach ($artifact in @($exe.FullName, $zip.FullName)) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $artifact
    $relativePath = $artifact.Substring($releaseDir.Length).TrimStart("\", "/")
    [PSCustomObject]@{
        FullName = $artifact
        RelativePath = $relativePath
        Hash = $hash.Hash
    }
}

$hashLines = $artifactHashes | ForEach-Object { "$($_.Hash)  $($_.RelativePath)" }
Set-Content -LiteralPath $hashPath -Value ($hashLines -join [Environment]::NewLine) -Encoding ASCII
$hashFile = Get-RequiredFile -Path $hashPath -Description "SHA256SUMS"
$exeHash = $artifactHashes | Where-Object { $_.FullName -eq $exe.FullName } | Select-Object -First 1
$zipHash = $artifactHashes | Where-Object { $_.FullName -eq $zip.FullName } | Select-Object -First 1

Write-Host ""
Write-Host "Portable build is ready:"
Write-Host "  Build: $releaseName"
Write-Host "  Commit: $shortCommit"
Write-Host "  EXE: $($exe.FullName)"
Write-Host "  EXE size: $([Math]::Round($exe.Length / 1MB, 2)) MB"
Write-Host "  EXE SHA256: $($exeHash.Hash)"
Write-Host "  ZIP: $($zip.FullName)"
Write-Host "  ZIP size: $([Math]::Round($zip.Length / 1MB, 2)) MB"
Write-Host "  ZIP SHA256: $($zipHash.Hash)"
Write-Host "  ZIP contents: $($zipEntries -join ', ')"
Write-Host "  SHA256SUMS: $($hashFile.FullName)"
Write-Host ""
Write-Host "For GitHub Release, upload the portable ZIP first. This is not an installer, and do not upload framework-dependent small exe files alone."
