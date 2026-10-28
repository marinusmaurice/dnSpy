<#
	Release smoke test, run by CI on each build that becomes a release zip:

		./Test/DecompilerStress/check-publish.ps1 -PublishDir <folder with dnSpy.exe and bin\>

	Checks that the build contains the C# (ILSpy 11) decompiler (its extension and the renamed ICSharpCode.Decompiler 11),
	then runs check.ps1 with that build's own dnSpy.Console.exe. v7.0.0-beta.2's .NET zips were missing the extension
	because a failed build wasn't noticed; this catches that.
#>
param(
	[Parameter(Mandatory = $true)][string]$PublishDir
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PublishDir)) {
	Write-Host "FAIL: $PublishDir doesn't exist" -ForegroundColor Red
	exit 1
}
$PublishDir = (Resolve-Path $PublishDir).Path
$failed = 0
foreach ($file in 'dnSpy.exe', 'dnSpy.Console.exe', 'bin\dnSpy.Decompiler.ILSpyModern.x.dll', 'bin\dnSpy.Decompiler.ILSpyModern.Core.dll', 'bin\dnSpy.ICSharpCode.Decompiler.dll', 'bin\ICSharpCode.Decompiler.dll') {
	if (-not (Test-Path (Join-Path $PublishDir $file))) {
		Write-Host "FAIL: missing $file" -ForegroundColor Red
		$failed++
	}
}
if ($failed -eq 0) {
	# The renamed copy must be the new engine, and the old fork must keep its name and version
	$renamed = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $PublishDir 'bin\dnSpy.ICSharpCode.Decompiler.dll'))
	$old = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $PublishDir 'bin\ICSharpCode.Decompiler.dll'))
	if ($renamed.Version.Major -lt 9) { Write-Host "FAIL: dnSpy.ICSharpCode.Decompiler is $($renamed.Version)" -ForegroundColor Red; $failed++ }
	if ($old.Version.Major -ne 2) { Write-Host "FAIL: ICSharpCode.Decompiler (old fork) is $($old.Version)" -ForegroundColor Red; $failed++ }
	Write-Host "OK: C# (ILSpy 11) files present (dnSpy.ICSharpCode.Decompiler $($renamed.Version), old ICSharpCode.Decompiler $($old.Version))"
}
if ($failed) {
	Write-Host "$failed check(s) failed" -ForegroundColor Red
	exit 1
}

& (Join-Path $PSScriptRoot 'check.ps1') -Console (Join-Path $PublishDir 'dnSpy.Console.exe')
exit $LASTEXITCODE
