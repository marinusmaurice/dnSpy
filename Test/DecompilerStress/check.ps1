<#
	Decompiler regression test for C# (ILSpy 11), run by CI and by hand:

		./Test/DecompilerStress/check.ps1 [-Console <path to dnSpy.Console.exe>]

	Builds DecompilerStress in Debug and Release, decompiles Program with dnSpy.Console (default language), and checks:
	  - no compiler names are left: <>h__TransparentIdentifier, if (1 == 0)
	  - every `let` clause is restored (sections 4 and 11)
	  - the decompiled code compiles and prints the same as the original (HashCode.Combine values are random per process)
	  - the debugger info lets the debugger stop at every statement and step over awaits (../DebugInfoCheck, .NET builds only)
	Exits with 1 when a check fails.
#>
param(
	[string]$Console = (Join-Path $PSScriptRoot '..\..\dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.Console.exe')
)
$ErrorActionPreference = 'Stop'
$failed = 0
$work = Join-Path ([IO.Path]::GetTempPath()) ("dnspy-decompiler-check-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work > $null

function Fail([string]$message) {
	Write-Host "FAIL: $message" -ForegroundColor Red
	$script:failed++
}

function Invoke-Checked([string]$name, [scriptblock]$action) {
	$output = & $action 2>&1
	if ($LASTEXITCODE) {
		$output | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" }
		throw "$name failed (exit code $LASTEXITCODE)"
	}
	return $output
}

# HashCode.Combine is seeded randomly per process
function Normalize([string[]]$lines) { $lines | ForEach-Object { $_ -replace '"Hash":-?\d+', '"Hash":0' } }

try {
	if (-not (Test-Path $Console)) { throw "dnSpy.Console not found: $Console" }
	$Console = (Resolve-Path $Console).Path
	$project = Join-Path $PSScriptRoot 'DecompilerStress.csproj'
	$expectedLets = 4	# section 4: gross, segment; section 11: size, band

	# A release build has the DLLs in bin\, a dev build next to dnSpy.Console.exe
	$dllDir = Split-Path $Console
	if (Test-Path (Join-Path $dllDir 'bin\dnlib.dll')) { $dllDir = Join-Path $dllDir 'bin' }
	$debugInfoCheck = $null
	if (Test-Path (Join-Path $dllDir 'dnSpy.Console.runtimeconfig.json')) {
		$debugInfoCheckDir = Join-Path $work 'DebugInfoCheck'
		Invoke-Checked "build DebugInfoCheck" { dotnet build (Join-Path $PSScriptRoot '..\DebugInfoCheck\DebugInfoCheck.csproj') -c Release -o $debugInfoCheckDir -v:q -nologo "-p:DnSpyBin=$dllDir" } > $null
		$debugInfoCheck = Join-Path $debugInfoCheckDir 'DebugInfoCheck.exe'
	}
	else {
		Write-Host '  (debugger info check skipped: it needs a .NET build of dnSpy)'
	}

	foreach ($configuration in 'Debug', 'Release') {
		Write-Host "== $configuration"
		$origDir = Join-Path $work "orig-$configuration"
		Invoke-Checked "build ($configuration)" { dotnet build $project -c $configuration -o $origDir -v:q -nologo } > $null
		$original = Normalize (Invoke-Checked "run original" { dotnet (Join-Path $origDir 'DecompilerStress.dll') })

		$decompiled = Invoke-Checked "decompile" { & $Console --no-color --no-tokens -t DecompilerStress.Program (Join-Path $origDir 'DecompilerStress.dll') }
		$code = $decompiled -join "`n"

		$ti = ([regex]::Matches($code, '<>h__TransparentIdentifier')).Count
		if ($ti -ne 0) { Fail "$configuration`: $ti <>h__TransparentIdentifier references" }
		$emptyIf = ([regex]::Matches($code, 'if \(1 == 0\)')).Count
		if ($emptyIf -ne 0) { Fail "$configuration`: $emptyIf 'if (1 == 0)' statements" }
		$lets = ([regex]::Matches($code, '(?m)^\s*let \w+ =')).Count
		if ($lets -ne $expectedLets) { Fail "$configuration`: $lets let clauses, expected $expectedLets" }
		foreach ($let in 'let gross =', 'let segment =', 'let size =', 'let band =') {
			if (-not $code.Contains($let)) { Fail "$configuration`: missing '$let'" }
		}

		if ($debugInfoCheck) {
			$debugInfo = & $debugInfoCheck $dllDir (Join-Path $origDir 'DecompilerStress.dll') 2>&1
			if ($LASTEXITCODE) {
				Fail "$configuration`: debugger info"
				$debugInfo | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" }
			}
			else {
				$debugInfo | ForEach-Object { Write-Host "  OK debugger info: $("$_".Trim())" }
			}
		}

		# The decompiled code must compile and behave like the original
		$decompDir = Join-Path $work "decompiled-$configuration"
		New-Item -ItemType Directory $decompDir > $null
		Copy-Item $project $decompDir
		Set-Content -Path (Join-Path $decompDir 'Program.cs') -Value $decompiled -Encoding utf8
		$binDir = Join-Path $decompDir 'out'
		$build = & dotnet build (Join-Path $decompDir 'DecompilerStress.csproj') -c Release -o $binDir -v:q -nologo 2>&1
		if ($LASTEXITCODE) {
			Fail "$configuration`: decompiled code doesn't compile"
			$build | Where-Object { $_ -match ' error ' } | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
			continue
		}
		$rerun = Normalize (Invoke-Checked "run decompiled" { dotnet (Join-Path $binDir 'DecompilerStress.dll') })
		$diff = Compare-Object $original $rerun
		if ($diff) {
			Fail "$configuration`: decompiled program prints something different"
			$diff | Select-Object -First 10 | ForEach-Object { Write-Host "  $($_.SideIndicator) $($_.InputObject)" }
		}
		else {
			Write-Host "  OK: no compiler names, $lets let clauses, compiles, same output ($($original.Count) lines)"
		}
	}

	# BAML decompiler: the XAML events and x:Names that the compiler moves to the code-behind's Connect methods must be
	# put back, and the exported project must build and wire up the same handlers (.NET and .NET Framework compilers)
	$wpfProject = Join-Path $PSScriptRoot '..\WpfEvents\WpfEvents.csproj'
	$msbuild = Get-Command msbuild -ErrorAction SilentlyContinue
	function Get-Handlers([string]$file) {
		$il = & $Console --no-color --no-tokens -l IL -t WpfEvents.MainWindow $file 2>&1
		($il | Select-String 'ldftn\s+instance void WpfEvents\.MainWindow::(\w+)' -AllMatches | ForEach-Object { $_.Matches.Groups[1].Value } | Sort-Object) -join ','
	}
	foreach ($tfm in 'net10.0-windows', 'net48') {
		foreach ($configuration in 'Debug', 'Release') {
			$name = "WPF $tfm $configuration"
			Write-Host "== $name"
			$origDir = Join-Path $work "wpf-$tfm-$configuration"
			Invoke-Checked "build WpfEvents ($tfm, $configuration)" { dotnet build $wpfProject -c $configuration -f $tfm -o $origDir -v:q -nologo } > $null
			$origFile = Join-Path $origDir 'WpfEvents.dll'
			if (-not (Test-Path $origFile)) { $origFile = Join-Path $origDir 'WpfEvents.exe' }
			$expected = Get-Handlers $origFile

			$exportDir = Join-Path $work "wpf-export-$tfm-$configuration"
			Invoke-Checked "export WpfEvents" { & $Console --no-color -o $exportDir $origFile } > $null
			$xaml = Get-Content (Get-ChildItem $exportDir -Recurse -Filter MainWindow.xaml | Select-Object -First 1).FullName -Raw
			$count = ([regex]::Matches($xaml, '="\w+_\w+"')).Count
			if ($count -ne 7) { Fail "$name`: $count of 7 event handlers in MainWindow.xaml" }
			if ($xaml -match '<!--') { Fail "$name`: MainWindow.xaml has error comments" }

			# .NET Framework exports are classic projects, which need MSBuild for WPF
			$exportProject = (Get-ChildItem $exportDir -Recurse -Filter *.csproj | Select-Object -First 1).FullName
			$rebuiltDir = Join-Path $work "wpf-rebuilt-$tfm-$configuration\"
			if ($tfm -eq 'net48') {
				if (-not $msbuild) { Write-Host '  (export build skipped: msbuild not found)'; continue }
				$build = & $msbuild.Source -nr:false -v:q -restore -p:Configuration=Release "-p:OutputPath=$rebuiltDir" $exportProject 2>&1
			}
			else {
				$build = & dotnet build $exportProject -c Release -o $rebuiltDir -v:q -nologo 2>&1
			}
			if ($LASTEXITCODE) {
				Fail "$name`: exported project doesn't build"
				$build | Where-Object { $_ -match ' error ' } | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
				continue
			}
			$actual = Get-Handlers (Join-Path $rebuiltDir (Split-Path $origFile -Leaf))
			if ($actual -ne $expected) { Fail "$name`: rebuilt app wires up '$actual', expected '$expected'" }
			else { Write-Host "  OK: $count XAML event handlers, export builds and wires up the same handlers" }
		}
	}

	# Export to Project: code shapes that used to export as code that doesn't compile (<>y__InlineArrayN locals declared
	# at the start of a method with `fixed` statements, a file-local type that ICSharpCode.Decompiler hid)
	$casesProject = Join-Path $PSScriptRoot '..\ExportCases\ExportCases.csproj'
	foreach ($configuration in 'Debug', 'Release') {
		$name = "Export cases $configuration"
		Write-Host "== $name"
		$origDir = Join-Path $work "cases-$configuration"
		Invoke-Checked "build ExportCases ($configuration)" { dotnet build $casesProject -c $configuration -o $origDir -v:q -nologo } > $null
		$original = Invoke-Checked "run ExportCases" { dotnet (Join-Path $origDir 'ExportCases.dll') }
		$exportDir = Join-Path $work "cases-export-$configuration"
		Invoke-Checked "export ExportCases" { & $Console --no-color -o $exportDir (Join-Path $origDir 'ExportCases.dll') } > $null
		$exportProject = (Get-ChildItem $exportDir -Recurse -Filter *.csproj | Select-Object -First 1).FullName
		$rebuiltDir = Join-Path $work "cases-rebuilt-$configuration"
		$build = & dotnet build $exportProject -c Release -o $rebuiltDir -v:q -nologo 2>&1
		if ($LASTEXITCODE) {
			Fail "$name`: exported project doesn't build"
			$build | Where-Object { $_ -match ' error ' } | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
			continue
		}
		$rerun = Invoke-Checked "run exported ExportCases" { dotnet (Join-Path $rebuiltDir 'ExportCases.dll') }
		if (Compare-Object $original $rerun) { Fail "$name`: exported program prints something different" }
		else { Write-Host "  OK: export builds and prints the same ($($original.Count) lines)" }
	}
}
catch {
	Fail $_.Exception.Message
}
finally {
	Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed) {
	Write-Host "$failed check(s) failed" -ForegroundColor Red
	exit 1
}
Write-Host 'All decompiler checks passed' -ForegroundColor Green
exit 0
