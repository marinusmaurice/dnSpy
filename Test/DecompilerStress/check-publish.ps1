# Copyright (C) 2026- marinusmaurice@outlook.com
<#
	Release smoke test, run by CI on each build that becomes a release zip:

		./Test/DecompilerStress/check-publish.ps1 -PublishDir <folder with dnSpy.exe and bin\>

	Checks that the build contains the C# (ILSpy 11) decompiler (its extension and the renamed ICSharpCode.Decompiler 11),
	and that an extension made with the extension template builds against it, then runs check.ps1 with that build's own
	dnSpy.Console.exe. v7.0.0-beta.2's .NET zips were missing the extension
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
if ($failed -eq 0) {
	# dotnet new dnspy-extension (templates/dnSpy.Extension): a new extension must build against this build's files, and
	# only its own files may end up in its build folder (dnSpy's are references with Private=false)
	$net48 = -not (Test-Path (Join-Path $PublishDir 'bin\dnSpy.runtimeconfig.json'))
	$framework = if ($net48) { 'net48' } else { 'net10.0-windows' }
	$work = Join-Path ([IO.Path]::GetTempPath()) ('dnspy-template-' + [guid]::NewGuid().ToString('N'))
	$oldCliHome = $env:DOTNET_CLI_HOME
	# The exit codes are checked: dotnet's stderr output mustn't stop the script (Windows PowerShell 5.1 makes it an error)
	$ErrorActionPreference = 'Continue'
	try {
		New-Item -ItemType Directory $work | Out-Null
		# Its own dotnet CLI home: the template isn't installed for the user running this
		$env:DOTNET_CLI_HOME = $work
		$template = (Resolve-Path (Join-Path $PSScriptRoot '..\..\templates\dnSpy.Extension')).Path
		$out = & dotnet new install $template 2>&1
		if ($LASTEXITCODE -ne 0) { throw "dotnet new install failed: $out" }
		$out = & dotnet new dnspy-extension -n TemplateCheck -o (Join-Path $work 'TemplateCheck') --dnspy-dir $PublishDir -f $framework 2>&1
		if ($LASTEXITCODE -ne 0) { throw "dotnet new dnspy-extension failed: $out" }
		$out = & dotnet build (Join-Path $work 'TemplateCheck\TemplateCheck.csproj') -nologo -v:q 2>&1
		if ($LASTEXITCODE -ne 0) { throw "the new extension didn't build: $($out -join ' ')" }
		$built = Get-ChildItem (Join-Path $work "TemplateCheck\bin\Debug\$framework") -File | ForEach-Object Name
		if ($built -notcontains 'TemplateCheck.x.dll') { throw "no TemplateCheck.x.dll in the build folder: $($built -join ', ')" }
		$extra = $built | Where-Object { $_ -notlike 'TemplateCheck.x.*' }
		if ($extra) { throw "dnSpy's files were copied to the build folder: $($extra -join ', ')" }
		Write-Host "OK: dotnet new dnspy-extension builds against this build ($framework)"
	}
	catch {
		Write-Host "FAIL: extension template: $_" -ForegroundColor Red
		if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error title=check-publish.ps1::extension template: $_" }
		$failed++
	}
	finally {
		$env:DOTNET_CLI_HOME = $oldCliHome
		Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
		$ErrorActionPreference = 'Stop'
	}
}
if ($failed) {
	Write-Host "$failed check(s) failed" -ForegroundColor Red
	exit 1
}

& (Join-Path $PSScriptRoot 'check.ps1') -Console (Join-Path $PublishDir 'dnSpy.Console.exe')
exit $LASTEXITCODE
