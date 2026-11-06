# Copyright (C) 2026- marinusmaurice@outlook.com
#
# Writes the package manager manifests for a release, from the release zips:
#   <OutDir>\dnspy.json                              Scoop (install with: scoop install <URL of dnspy.json>)
#   <OutDir>\winget\<PackageIdentifier>.*.yaml       winget (submit to microsoft/winget-pkgs)
# Run by the release workflow (.github/workflows/build.yml); the files are attached to the GitHub release.
#
#   .\New-PackageManifests.ps1 -Version 7.26.0.0 -ZipDir C:\builtfiles -OutDir C:\builtfiles\packaging -Repository marinusmaurice/dnSpy

param(
	[Parameter(Mandatory)] [string] $Version,
	[Parameter(Mandatory)] [string] $ZipDir,
	[Parameter(Mandatory)] [string] $OutDir,
	[string] $Repository = 'marinusmaurice/dnSpy',
	[string] $PackageIdentifier = 'marinusmaurice.dnSpy'
)

$ErrorActionPreference = 'Stop'

$tag = "v$Version"
$baseUrl = "https://github.com/$Repository/releases/download/$tag"
$zip64 = Join-Path $ZipDir 'dnSpy-net-win64.zip'
$zip32 = Join-Path $ZipDir 'dnSpy-net-win32.zip'
foreach ($zip in $zip64, $zip32) {
	if (-not (Test-Path $zip)) { throw "Missing $zip" }
}
$hash64 = (Get-FileHash $zip64 -Algorithm SHA256).Hash
$hash32 = (Get-FileHash $zip32 -Algorithm SHA256).Hash

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Scoop: the self-contained .NET zips, so no runtime is needed. checkver/autoupdate let a bucket update it.
$scoop = [ordered]@{
	version = $Version
	description = 'Debugger and .NET assembly editor'
	homepage = "https://github.com/$Repository"
	license = 'GPL-3.0-only'
	architecture = [ordered]@{
		'64bit' = [ordered]@{ url = "$baseUrl/dnSpy-net-win64.zip"; hash = $hash64.ToLowerInvariant() }
		'32bit' = [ordered]@{ url = "$baseUrl/dnSpy-net-win32.zip"; hash = $hash32.ToLowerInvariant() }
	}
	bin = @('dnSpy.Console.exe')
	shortcuts = @(, @('dnSpy.exe', 'dnSpy'))
	# An empty dnSpy.xml next to dnSpy.exe = portable mode: the settings stay in the app folder, which Scoop keeps
	persist = @('dnSpy.xml')
	pre_install = 'if (-not (Test-Path "$dir\dnSpy.xml")) { Set-Content -Path "$dir\dnSpy.xml" -Value "" -Encoding Ascii }'
	checkver = [ordered]@{ github = "https://github.com/$Repository" }
	autoupdate = [ordered]@{
		architecture = [ordered]@{
			'64bit' = [ordered]@{ url = "https://github.com/$Repository/releases/download/v`$version/dnSpy-net-win64.zip" }
			'32bit' = [ordered]@{ url = "https://github.com/$Repository/releases/download/v`$version/dnSpy-net-win32.zip" }
		}
	}
}
[System.IO.File]::WriteAllText((Join-Path $OutDir 'dnspy.json'), ($scoop | ConvertTo-Json -Depth 10), $utf8)

# winget: multi-file manifest, portable app in a zip
$wingetDir = Join-Path $OutDir 'winget'
New-Item -ItemType Directory -Force -Path $wingetDir | Out-Null
$schemaVersion = '1.6.0'

$versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schemaVersion.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schemaVersion
"@

$installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schemaVersion.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: dnSpy.exe
  PortableCommandAlias: dnspy
Installers:
- Architecture: x64
  InstallerUrl: $baseUrl/dnSpy-net-win64.zip
  InstallerSha256: $hash64
- Architecture: x86
  InstallerUrl: $baseUrl/dnSpy-net-win32.zip
  InstallerSha256: $hash32
ManifestType: installer
ManifestVersion: $schemaVersion
"@

$localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schemaVersion.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: marinusmaurice
PublisherUrl: https://github.com/marinusmaurice
PackageName: dnSpy
PackageUrl: https://github.com/$Repository
License: GPL-3.0
LicenseUrl: https://github.com/$Repository/blob/master/dnSpy/dnSpy/LicenseInfo/GPLv3.txt
ShortDescription: Debugger and .NET assembly editor
Description: Decompile, debug and edit .NET programs without their source code. A continuation of the original dnSpy for current .NET.
Tags:
- debugger
- decompiler
- dotnet
- reverse-engineering
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/$tag
ManifestType: defaultLocale
ManifestVersion: $schemaVersion
"@

[System.IO.File]::WriteAllText((Join-Path $wingetDir "$PackageIdentifier.yaml"), $versionYaml + "`n", $utf8)
[System.IO.File]::WriteAllText((Join-Path $wingetDir "$PackageIdentifier.installer.yaml"), $installerYaml + "`n", $utf8)
[System.IO.File]::WriteAllText((Join-Path $wingetDir "$PackageIdentifier.locale.en-US.yaml"), $localeYaml + "`n", $utf8)

Write-Host "Wrote $OutDir\dnspy.json and $wingetDir"
