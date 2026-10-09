# Chocolatey Package Builder
#
# This script downloads the installer from GitHub releases and creates a Chocolatey package.
# It calculates the SHA256 hash for the installer and generates the required files.
#
# Requirements:
#   - Installer filename should follow the pattern: {name}_v{version}_Setup.exe
#
# Usage:
#   .\build.ps1 -Publisher "owner" -Name "repo" -Version "1.0.0"
#
# Required Parameters:
#   -Publisher    : The repository owner/publisher name
#   -Name         : The repository/package name
#   -Version      : The version to publish (without 'v' prefix)
#
# Optional Parameters:
#   -ApiKey       : Chocolatey API key for publishing
#   -Description  : Package description for the nuspec
#   -Summary      : Short summary for the nuspec
#   -InstallerPath: Hash this local installer instead of downloading it from the release.
#                   The package carries no binaries — only a script that downloads the installer — so
#                   its checksum has to describe the *published* file. Passing the file that was just
#                   built (the same bytes that get uploaded) skips a 23 MB download and lets the
#                   package be built before the release exists, which is what tools/release.py does.
#   -Repository   : The GitHub repository the release lives in, when it differs from the package name.
#                   Defaults to $Name (the upstream layout, where both are the same). It matters:
#                   this repository is saillill/ExplorerTabUtility-WinUI3 while the package is
#                   explorerTabUtility, and the release asset is ExplorerTabUtility_v1.0.1_Setup.exe —
#                   three different names. Using one of them for all three is exactly how the release
#                   download 404s, which is what killed the "Publish to Chocolatey" workflow.

Param
(
    [parameter(Mandatory = $true)]
    [string]
    $Publisher,
    [parameter(Mandatory = $true)]
    [string]
    $Name,
    [parameter(Mandatory = $true)]
    [string]
    $Version,
    [parameter(Mandatory = $false)]
    [string]
    $ApiKey,
    [parameter(Mandatory = $false)]
    [string]
    $Description,
    [parameter(Mandatory = $false)]
    [string]
    $Summary,
    [parameter(Mandatory = $false)]
    [string]
    $InstallerPath,
    [parameter(Mandatory = $false)]
    [string]
    $Repository
)

function Get-ArtifactHash
{
    # Local installer: hash the file we are about to publish. Without this the checksum always comes
    # from whatever is on the release right now, which during a release is the *previous* build.
    if ($InstallerPath)
    {
        if (-not (Test-Path $InstallerPath))
        {
            throw "InstallerPath does not exist: $InstallerPath"
        }

        Write-Host "Hashing local installer: $InstallerPath"
        return (Get-FileHash -Algorithm SHA256 $InstallerPath).Hash.ToLower()
    }

    # Create temp directory
    $tempDir = Join-Path $env:TEMP "choco_artifacts_$([Guid]::NewGuid().ToString() )"
    New-Item -ItemType Directory -Path $tempDir | Out-Null

    try
    {
        $fileName = "$( $Name )_v$( $Version )_Setup.exe"
        $downloadUrl = "https://github.com/$Publisher/$Repository/releases/download/v$Version/$fileName"
        $outputPath = Join-Path $tempDir $fileName

        # Download the file
        Write-Host "Downloading installer from: $downloadUrl"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $outputPath -UseBasicParsing

        # Calculate SHA256
        $hash = (Get-FileHash -Algorithm SHA256 $outputPath).Hash
        return $hash.ToLower()
    }
    finally
    {
        # Cleanup temp directory
        if (Test-Path $tempDir)
        {
            Remove-Item -Path $tempDir -Recurse -Force
        }
    }
}

function Write-TemplateFile
{
    param (
        [parameter(Mandatory = $true)]
        [string]
        $SourceFile,
        [parameter(Mandatory = $true)]
        [string]
        $DestinationFile
    )
    $content = Get-Content $SourceFile -Raw

    # Basic replacements
    $content = $content.Replace('{{VERSION}}', $Version)
    $content = $content.Replace('{{PUBLISHER}}', $Publisher)
    $content = $content.Replace('{{PACKAGE_ID}}',$Name.ToLower())
    $content = $content.Replace('{{PACKAGE_NAME}}', $Name)
    $content = $content.Replace('{{REPOSITORY}}', $Repository)
    $content = $content.Replace('{{DESCRIPTION}}', $Description)
    $content = $content.Replace('{{SUMMARY}}', $Summary)
    $content = $content.Replace('{{CHECKSUM}}', $Checksum)

    $content | Out-File -Encoding 'UTF8' $DestinationFile
}

# Clean version string
$Version = $Version.TrimStart('v')

# Repository: defaults to the package name so an upstream-style caller keeps working.
if (-not $Repository)
{
    $Repository = $Name
}

#Description
if (-not $Description)
{
    $Description = $Name
}

# Summary
if (-not $Summary)
{
    $Summary = $Name
}

# Create the package directory
$packageDir = Join-Path $PWD "package"
if (Test-Path $packageDir)
{
    Remove-Item -Path $packageDir -Recurse -Force
}
New-Item -Path $PWD -Name "package" -ItemType "directory" | Out-Null
New-Item -Path $packageDir -Name "tools" -ItemType "directory" | Out-Null

# Download artifacts and calculate hashes
$Checksum = Get-ArtifactHash

# Process template files
Write-TemplateFile -SourceFile "templates\PACKAGE_NAME.nuspec" -DestinationFile "package\$Name.nuspec"
Write-TemplateFile -SourceFile "templates\tools\chocolateyinstall.ps1" -DestinationFile "package\tools\chocolateyinstall.ps1"
Write-TemplateFile -SourceFile "templates\tools\chocolateyuninstall.ps1" -DestinationFile "package\tools\chocolateyuninstall.ps1"

# ANSI color codes
$green = "`e[32m"
$cyan = "`e[36m"
$reset = "`e[0m"

Write-Output "`n${green}=== Generated Chocolatey package files ===${reset}`n"
Get-ChildItem ".\package\*.nuspec", ".\package\tools\*.ps1" | ForEach-Object {
    Write-Output "${cyan}=== Contents of $( $_.Name ) ===${reset}`n"
    Get-Content $_.FullName
    Write-Output "`n${green}===============================${reset}`n"
}

# Create the package
Write-Output "`n${green}=== Building Chocolatey package ===${reset}`n"
choco pack "package\$Name.nuspec" --out "."

# Push the package if API key is provided
if ($ApiKey)
{
    Write-Output "`n${green}=== Pushing package to Chocolatey ===${reset}`n"
    choco push "$Name.$Version.nupkg" --source=https://push.chocolatey.org/ --api-key=$ApiKey
}
