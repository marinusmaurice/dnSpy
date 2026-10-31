<#
	Copyright (C) 2026- marinusmaurice@outlook.com
	Decompiler regression test for C# (ILSpy 11), run by CI and by hand:

		./Test/DecompilerStress/check.ps1 [-Console <path to dnSpy.Console.exe>]

	Builds DecompilerStress in Debug and Release, decompiles Program with dnSpy.Console (default language), and checks:
	  - no compiler names are left: <>h__TransparentIdentifier, if (1 == 0)
	  - every `let` clause is restored (sections 4 and 11)
	  - the decompiled code compiles and prints the same as the original (HashCode.Combine values are random per process)
	  - the debugger info lets the debugger stop at every statement and step over awaits (../DebugInfoCheck, .NET builds only)
	  - the Debug History (time-travel debugging) ring buffer and Step Back/Forward (../DebugHistoryCheck)
	  - the command palette's fuzzy matching (../CommandPaletteCheck)
	  - the side-by-side view's language choice and tab sync (../SideBySideCheck)
	  - patch export/import: a patch made from ../PatchCases and its edited version, applied to a newer version (../PatchCheck)
	  - the tracepoint message keywords ($HITCOUNT, $TICK, $TIME, ...) (../TracepointCheck)
	  - pinned and recent files in File -> Recent Files (../RecentFilesCheck)
	  - Open from NuGet's package sources from NuGet.config, search and download over V3, V2 and folder feeds (../NuGetFeedCheck)
	  - portable mode: where the settings and data go with a dnSpy.xml next to dnSpy.exe or in bin (../PortableCheck)
	  - the assemblies in Android packages: XALZ (LZ4) compressed files and assembly stores (../AndroidPackageCheck)
	  - the assembly diff of ../DiffCases as text and as JSON (--json)
	  - the type and member list of ../DiffCases as text and as JSON (--list, --list --json)
	  - obfuscator/packer detection of ../ProtectionCases' assembly and of DiffCases (--protection, --protection --json)
	  - response files (@file) and batch files (--batch file)
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
	# put back, and the exported project must build and wire up the same handlers (.NET and .NET Framework compilers).
	# WpfEventsVB is the same app in VB (its x:Names are properties, its Connect methods are renamed), exported as C#.
	$msbuild = Get-Command msbuild -ErrorAction SilentlyContinue
	function Get-Handlers([string]$file, [string]$app) {
		$il = & $Console --no-color --no-tokens -l IL -t "$app.MainWindow" $file 2>&1
		($il | Select-String "ldftn\s+instance void $app\.MainWindow::(\w+)" -AllMatches | ForEach-Object { $_.Matches.Groups[1].Value } | Sort-Object) -join ','
	}
	foreach ($wpfProject in (Join-Path $PSScriptRoot '..\WpfEvents\WpfEvents.csproj'), (Join-Path $PSScriptRoot '..\WpfEventsVB\WpfEventsVB.vbproj')) {
		$app = [IO.Path]::GetFileNameWithoutExtension($wpfProject)
		$label = if ($app -eq 'WpfEvents') { 'WPF' } else { 'WPF VB' }
		# dotnet build can't compile VB WPF apps for .NET Framework
		$tfms = if ($app -eq 'WpfEvents') { 'net10.0-windows', 'net48' } else { 'net10.0-windows' }
		foreach ($tfm in $tfms) {
			foreach ($configuration in 'Debug', 'Release') {
				$name = "$label $tfm $configuration"
				Write-Host "== $name"
				$origDir = Join-Path $work "$app-$tfm-$configuration"
				Invoke-Checked "build $app ($tfm, $configuration)" { dotnet build $wpfProject -c $configuration -f $tfm -o $origDir -v:q -nologo } > $null
				$origFile = Join-Path $origDir "$app.dll"
				if (-not (Test-Path $origFile)) { $origFile = Join-Path $origDir "$app.exe" }
				$expected = Get-Handlers $origFile $app

				$exportDir = Join-Path $work "$app-export-$tfm-$configuration"
				Invoke-Checked "export $app" { & $Console --no-color -o $exportDir $origFile } > $null
				$xaml = Get-Content (Get-ChildItem $exportDir -Recurse -Filter MainWindow.xaml | Select-Object -First 1).FullName -Raw
				$count = ([regex]::Matches($xaml, '="\w+_\w+"')).Count
				if ($count -ne 7) { Fail "$name`: $count of 7 event handlers in MainWindow.xaml" }
				if ($xaml -match '<!--') { Fail "$name`: MainWindow.xaml has error comments" }

				# .NET Framework exports are classic projects, which need MSBuild for WPF
				$exportProject = (Get-ChildItem $exportDir -Recurse -Filter *.csproj | Select-Object -First 1).FullName
				$rebuiltDir = Join-Path $work "$app-rebuilt-$tfm-$configuration\"
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
				$actual = Get-Handlers (Join-Path $rebuiltDir (Split-Path $origFile -Leaf)) $app
				if ($actual -ne $expected) { Fail "$name`: rebuilt app wires up '$actual', expected '$expected'" }
				else { Write-Host "  OK: $count XAML event handlers, export builds and wires up the same handlers" }
			}
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

	# Debug History: dnSpy.Debugger's snapshot/history classes compiled on their own (no debugged process needed)
	Write-Host '== Debug History'
	$historyCheck = Invoke-Checked "run DebugHistoryCheck" { dotnet run --project (Join-Path $PSScriptRoot '..\DebugHistoryCheck\DebugHistoryCheck.csproj') -c Release -v:q -nologo }
	if (-not ($historyCheck -contains 'All history checks passed')) { Fail 'Debug History: checks failed'; $historyCheck | ForEach-Object { Write-Host "  $_" } }
	else { Write-Host '  OK: ring buffer, Step Back/Forward, limits, value diff, time line' }

	# Command palette: the fuzzy matcher and the type/member texts compiled on their own (no WPF needed)
	Write-Host '== Command palette'
	$paletteCheck = Invoke-Checked "run CommandPaletteCheck" { dotnet run --project (Join-Path $PSScriptRoot '..\CommandPaletteCheck\CommandPaletteCheck.csproj') -c Release -v:q -nologo }
	if (-not ($paletteCheck -contains 'All command palette checks passed')) { Fail 'Command palette: checks failed'; $paletteCheck | ForEach-Object { Write-Host "  $_" } }
	else { Write-Host '  OK: fuzzy matching, order, access keys, type/member texts' }

	# Side-by-side view: the language shown next to the current one and the tab pair, compiled on their own (no WPF needed)
	Write-Host '== Side-by-side view'
	$sideCheck = Invoke-Checked "run SideBySideCheck" { dotnet run --project (Join-Path $PSScriptRoot '..\SideBySideCheck\SideBySideCheck.csproj') -c Release -v:q -nologo }
	if (-not ($sideCheck -contains 'All side-by-side checks passed')) { Fail 'Side-by-side view: checks failed'; $sideCheck | ForEach-Object { Write-Host "  $_" } }
	else { Write-Host '  OK: language picked for the second view, tab pair and sync guard' }

	# Tracepoints: dnSpy.Debugger's tracepoint message parser compiled on its own (no debugged process needed)
	Write-Host '== Tracepoints'
	$traceCheck = Invoke-Checked "run TracepointCheck" { dotnet run --project (Join-Path $PSScriptRoot '..\TracepointCheck\TracepointCheck.csproj') -c Release -v:q -nologo }
	if (-not ($traceCheck -contains 'All tracepoint checks passed')) { Fail 'Tracepoints: checks failed'; $traceCheck | ForEach-Object { Write-Host "  $_" } }
	else { Write-Host '  OK: tracepoint keywords ($HITCOUNT, $TICK, $TIME), escapes, expressions, help text' }

	# Recent files: dnSpy's recent file list (pinning) compiled on its own
	Write-Host '== Recent files'
	$recentCheck = Invoke-Checked "run RecentFilesCheck" { dotnet run --project (Join-Path $PSScriptRoot '..\RecentFilesCheck\RecentFilesCheck.csproj') -c Release -v:q -nologo }
	if (-not ($recentCheck -contains 'All recent files checks passed')) { Fail 'Recent files: checks failed'; $recentCheck | ForEach-Object { Write-Host "  $_" } }
	else { Write-Host '  OK: pinned files stay at the top and are not pushed out, unpin, order, limits' }

	# Open from NuGet: NuGet.config package sources, search and download (fake feeds, no network)
	Write-Host '== Open from NuGet, package sources'
	$nugetCheck = Invoke-Checked "run NuGetFeedCheck" { dotnet run --project (Join-Path $PSScriptRoot '..\NuGetFeedCheck\NuGetFeedCheck.csproj') -c Release -v:q -nologo }
	if (-not ($nugetCheck -contains 'All NuGet feed checks passed')) { Fail 'Open from NuGet: checks failed'; $nugetCheck | ForEach-Object { Write-Host "  $_" } }
	else { Write-Host '  OK: NuGet.config sources (clear, remove, disabled, relative paths), search and download over V3, V2 and folder feeds' }

	# Portable mode: AppDirectories compiled on its own, run from dnSpy-like folder layouts
	Write-Host '== Portable mode'
	$portableCheck = Invoke-Checked "run PortableCheck" { dotnet run --project (Join-Path $PSScriptRoot '..\PortableCheck\PortableCheck.csproj') -c Release -v:q -nologo }
	if (-not ($portableCheck -contains 'All portable mode checks passed')) { Fail 'Portable mode: checks failed'; $portableCheck | ForEach-Object { Write-Host "  $_" } }
	else { Write-Host '  OK: dnSpy.xml next to dnSpy.exe or in bin keeps settings and data in the dnSpy folder, otherwise %APPDATA%' }

	# Android packages: the reader compiled on its own, test .apk/.aab files written in memory
	Write-Host '== Android packages (.apk, .aab)'
	$androidCheck = Invoke-Checked "run AndroidPackageCheck" { dotnet run --project (Join-Path $PSScriptRoot '..\AndroidPackageCheck\AndroidPackageCheck.csproj') -c Release -v:q -nologo }
	if (-not ($androidCheck -contains 'All Android package checks passed')) { Fail 'Android packages: checks failed'; $androidCheck | ForEach-Object { Write-Host "  $_" } }
	else { Write-Host '  OK: plain and XALZ (LZ4) assemblies, assembly stores with assemblies.manifest, .aab and Unity layouts, broken data' }

	# Patch export/import: a patch made from PatchCases v1 and edited v1, applied to v2 (more members, other tokens), must
	# give the same method bodies as edited v2 and print the same
	Write-Host '== Patch export/import'
	$patchProject = Join-Path $PSScriptRoot '..\PatchCases\PatchCases.csproj'
	$patchDirs = @{}
	foreach ($variant in @(@('v1', '1', 'false'), @('v1p', '1', 'true'), @('v2', '2', 'false'), @('v2p', '2', 'true'))) {
		$dir = Join-Path $work "patch-$($variant[0])"
		Invoke-Checked "build PatchCases $($variant[0])" { dotnet build $patchProject -c Release -o $dir -v:q -nologo "-p:PatchVersion=$($variant[1])" "-p:Patched=$($variant[2])" } > $null
		$patchDirs[$variant[0]] = $dir
	}
	$patchOut = Join-Path $work 'patch-applied'
	Copy-Item $patchDirs['v2'] $patchOut -Recurse
	$patchCheckDir = Join-Path $work 'PatchCheck'
	Invoke-Checked "build PatchCheck" { dotnet build (Join-Path $PSScriptRoot '..\PatchCheck\PatchCheck.csproj') -c Release -o $patchCheckDir -v:q -nologo } > $null
	$patchCheck = & (Join-Path $patchCheckDir 'PatchCheck.exe') (Join-Path $patchDirs['v1'] 'PatchCases.dll') (Join-Path $patchDirs['v1p'] 'PatchCases.dll') `
		(Join-Path $patchDirs['v2'] 'PatchCases.dll') (Join-Path $patchDirs['v2p'] 'PatchCases.dll') (Join-Path $patchOut 'PatchCases.dll') 2>&1
	if ($LASTEXITCODE -or -not ($patchCheck -contains 'All patch checks passed')) { Fail 'Patch export/import: checks failed'; $patchCheck | ForEach-Object { Write-Host "  $_" } }
	else {
		$expectedRun = Invoke-Checked "run edited PatchCases v2" { dotnet (Join-Path $patchDirs['v2p'] 'PatchCases.dll') }
		$patchedRun = Invoke-Checked "run patched PatchCases v2" { dotnet (Join-Path $patchOut 'PatchCases.dll') }
		if (Compare-Object $expectedRun $patchedRun) { Fail 'Patch export/import: patched v2 prints something different'; $patchedRun | ForEach-Object { Write-Host "  $_" } }
		else { Write-Host "  OK: $($patchCheck[0]), applied to a newer version, same output ($($expectedRun.Count) lines)" }

		# The same with dnSpy.Console --patch-create / --patch-apply
		$consolePatch = Join-Path $work 'console.dnspypatch'
		$consoleOut = Join-Path $work 'patch-console'
		Copy-Item $patchDirs['v2'] $consoleOut -Recurse
		$created = @(Invoke-Checked "dnSpy.Console --patch-create" { & $Console --patch-create $consolePatch (Join-Path $patchDirs['v1'] 'PatchCases.dll') (Join-Path $patchDirs['v1p'] 'PatchCases.dll') })
		$applied = @(Invoke-Checked "dnSpy.Console --patch-apply" { & $Console --patch-apply $consolePatch -o (Join-Path $consoleOut 'PatchCases.dll') (Join-Path $patchDirs['v2'] 'PatchCases.dll') })
		$consoleRun = Invoke-Checked "run console-patched PatchCases v2" { dotnet (Join-Path $consoleOut 'PatchCases.dll') }
		$createdMethods = @($created | Where-Object { $_.StartsWith('+ ') }).Count
		$appliedMethods = @($applied | Where-Object { $_.StartsWith('+ ') }).Count
		if ($createdMethods -ne 4 -or $appliedMethods -ne 4 -or @($applied | Where-Object { $_.StartsWith('! ') }).Count -ne 0) {
			Fail "Patch export/import: dnSpy.Console made $createdMethods and applied $appliedMethods methods (expected 4)"
			$created + $applied | ForEach-Object { Write-Host "  $_" }
		}
		elseif (Compare-Object $expectedRun $consoleRun) { Fail 'Patch export/import: v2 patched by dnSpy.Console prints something different'; $consoleRun | ForEach-Object { Write-Host "  $_" } }
		else { Write-Host '  OK: dnSpy.Console --patch-create / --patch-apply, same output' }
	}

	# Assembly diff: the two versions of Test/DiffCases differ exactly in the members marked V2; a lambda, an iterator
	# and an unchanged type must not show up
	Write-Host "== Assembly diff"
	$diffProject = Join-Path $PSScriptRoot '..\DiffCases\DiffCases.csproj'
	$diffV1 = Join-Path $work 'diff-v1'
	$diffV2 = Join-Path $work 'diff-v2'
	Invoke-Checked "build DiffCases v1" { dotnet build $diffProject -c Release -o $diffV1 -v:q -nologo } > $null
	Invoke-Checked "build DiffCases v2" { dotnet build $diffProject -c Release -p:DiffVersion=2 -o $diffV2 -v:q -nologo } > $null
	$diffFile1 = Join-Path $diffV1 'DiffCases.dll'
	$diffFile2 = Join-Path $diffV2 'DiffCases.dll'
	$expectedDiff = @(
		'// Types: 1 added, 1 removed, 2 changed, 1 renamed'
		'// Resources: 1 added, 1 removed, 2 changed'
		'// Assembly version: 1.0.0.0 -> 2.0.0.0'
		''
		'~ type DiffCases.Calculator'
		'    ~ method <>c::System.Int32 <SumOfSquares>b__4_0(System.Int32)  // body'
		'    ~ method System.Int32 Add(System.Int32,System.Int32)  // body'
		'    - method System.Int32 Divide(System.Int32,System.Int32)'
		'    + method System.Int32 Multiply(System.Int32,System.Int32)'
		'    ~ method System.Int32 Opposite(System.Int32)  // renamed from System.Int32 Negate(System.Int32)'
		'    ~ property System.Int32 LastResult  // renamed from System.Int32 Last'
		'~ type DiffCases.Coordinates  // renamed from DiffCases.Point'
		'+ type DiffCases.Logger'
		'- type DiffCases.OldHelper'
		'~ type DiffCases.Settings'
		'    ~ field System.Int32 Timeout  // value'
		'    + property System.String Name'
		'~ resource DiffCases.Data.txt  // data (9 -> 14 bytes)'
		'+ resource DiffCases.New.txt'
		'- resource DiffCases.Old.txt'
		'~ resource DiffCases.Strings.resources  // data (261 -> 274 bytes)'
		'    - Bye'
		'    ~ Hello'
		'    + Welcome'
	)
	# The first two lines are the old and new assembly names and paths
	$actualDiff = @(Invoke-Checked "diff DiffCases" { & $Console --no-color --diff $diffFile1 $diffFile2 } | Select-Object -Skip 2)
	$sameDiff = @(Invoke-Checked "diff DiffCases with itself" { & $Console --no-color --diff $diffFile1 $diffFile1 } | Select-Object -Skip 2)
	$codeDiff = @(Invoke-Checked "code diff DiffCases" { & $Console --no-color --diff-code $diffFile1 $diffFile2 })
	# A patch can't hold renames and resources yet, so they're listed as not included
	$diffPatchFile = Join-Path $work 'diff.dnspypatch'
	$diffPatch = @(Invoke-Checked "patch-create DiffCases" { & $Console --patch-create $diffPatchFile $diffFile1 $diffFile2 })
	if (($expectedDiff -join "`n") -ne ($actualDiff -join "`n")) {
		Fail 'Assembly diff: unexpected differences'
		$actualDiff | ForEach-Object { Write-Host "  $_" }
	}
	elseif (-not ($sameDiff -contains '// No differences')) { Fail 'Assembly diff: an assembly differs from itself' }
	elseif (-not ($codeDiff -contains '+ 		lastResult = checked(a + b);' -and $codeDiff -contains '- 		lastResult = a + b;')) { Fail 'Assembly diff: the code diff is missing the change in Add' }
	elseif (-not ($codeDiff -contains '// Renamed: DiffCases.Point -> DiffCases.Coordinates')) { Fail 'Assembly diff: the code diff is missing the renamed type' }
	elseif (-not ($diffPatch -contains '! not included: renamed type DiffCases.Point -> DiffCases.Coordinates' -and $diffPatch -contains '! not included: added resource DiffCases.New.txt')) {
		Fail 'Assembly diff: the patch of DiffCases does not list the renamed type and the added resource as not included'
		$diffPatch | ForEach-Object { Write-Host "  $_" }
	}
	else { Write-Host "  OK: $($expectedDiff.Count - 4) lines of differences (incl. renames and resources), none with itself, code diff" }

	# --json: the same differences as JSON (docs/CONSOLE_JSON.md); the counts must match the text summary
	Write-Host "== Assembly diff as JSON"
	$jsonText = @(Invoke-Checked "diff DiffCases as JSON" { & $Console --diff-code --json $diffFile1 $diffFile2 }) -join "`n"
	$sameJsonText = @(Invoke-Checked "diff DiffCases with itself as JSON" { & $Console --diff --json $diffFile1 $diffFile1 }) -join "`n"
	$textSummary = @(Invoke-Checked "diff DiffCases" { & $Console --no-color --diff $diffFile1 $diffFile2 })[2]
	$json = $jsonText | ConvertFrom-Json
	$sameJson = $sameJsonText | ConvertFrom-Json
	$t = $json.summary.types
	$jsonSummary = "// Types: $($t.added) added, $($t.removed) removed, $($t.changed) changed" + $(if ($t.renamed) { ", $($t.renamed) renamed" } else { '' })
	$renamedType = @($json.types | Where-Object { $_.fullName -eq 'DiffCases.Coordinates' })
	$calculator = @($json.types | Where-Object { $_.fullName -eq 'DiffCases.Calculator' })
	$codeTexts = @($calculator | ForEach-Object { $_.code } | Where-Object { $_.kind -eq 'added' -or $_.kind -eq 'removed' } | ForEach-Object { "$($_.kind) $($_.text.Trim())" })
	if ($json.formatVersion -ne 1) { Fail "Assembly diff JSON: formatVersion is $($json.formatVersion), expected 1" }
	elseif ($jsonSummary -ne $textSummary) { Fail "Assembly diff JSON: summary '$jsonSummary' differs from the text summary '$textSummary'" }
	elseif (@($json.types).Count -ne @($expectedDiff | Where-Object { $_ -match '^[-+~] type ' }).Count) { Fail "Assembly diff JSON: $(@($json.types).Count) types, expected the same as the text output" }
	elseif ($renamedType.Count -ne 1 -or $renamedType[0].oldFullName -ne 'DiffCases.Point' -or $renamedType[0].kind -ne 'renamed' -or -not $renamedType[0].oldToken) { Fail 'Assembly diff JSON: the renamed type is missing or wrong' }
	elseif (-not (@($json.resources | Where-Object { $_.name -eq 'DiffCases.New.txt' -and $_.kind -eq 'added' }).Count -eq 1)) { Fail 'Assembly diff JSON: the added resource is missing' }
	elseif (-not ($codeTexts -contains 'added lastResult = checked(a + b);' -and $codeTexts -contains 'removed lastResult = a + b;')) { Fail 'Assembly diff JSON: the code diff is missing the change in Add'; $codeTexts | ForEach-Object { Write-Host "  $_" } }
	elseif (-not $sameJson.summary.identical -or @($sameJson.types).Count -ne 0) { Fail 'Assembly diff JSON: an assembly differs from itself' }
	elseif ($json.summary.identical) { Fail 'Assembly diff JSON: two different versions are identical' }
	else { Write-Host "  OK: valid JSON, same summary as the text output, renamed type, added resource, code diff" }

	# --list: the types and members of an assembly, as text and as JSON (docs/CONSOLE_JSON.md)
	Write-Host "== Type and member list"
	$listText = @(Invoke-Checked "list DiffCases" { & $Console --list $diffFile1 })
	$listJson = (@(Invoke-Checked "list DiffCases as JSON" { & $Console --list --json $diffFile1 }) -join "`n") | ConvertFrom-Json
	$listTypes = @($listJson.modules | ForEach-Object { $_.types })
	$listMembers = @($listTypes | ForEach-Object { $_.members })
	$listCount = "// $($listTypes.Count) types, $($listMembers.Count) members"
	$jsonCalculator = @($listTypes | Where-Object { $_.fullName -eq 'DiffCases.Calculator' })
	$jsonAdd = @($jsonCalculator | ForEach-Object { $_.members } | Where-Object { $_.name -eq 'System.Int32 Add(System.Int32,System.Int32)' })
	$jsonOldHelperName = @($listTypes | Where-Object { $_.fullName -eq 'DiffCases.OldHelper' } | ForEach-Object { $_.members } | Where-Object { $_.memberKind -eq 'property' })
	if (-not ($listText | Where-Object { $_ -match '^class public DiffCases\.Calculator  // 0x02[0-9A-F]{6}$' })) { Fail 'List: DiffCases.Calculator is missing'; $listText | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" } }
	elseif (-not ($listText | Where-Object { $_ -match '^    method public System\.Int32 Add\(System\.Int32,System\.Int32\)  // 0x06[0-9A-F]{6}$' })) { Fail 'List: Calculator.Add is missing' }
	elseif (-not ($listText | Where-Object { $_ -match '^    property public static System\.String Name  // 0x17[0-9A-F]{6}$' })) { Fail 'List: the static property OldHelper.Name is missing' }
	elseif (-not ($listText | Where-Object { $_ -match '^    field private System\.Int32 lastResult  // ' })) { Fail 'List: the private field Calculator.lastResult is missing' }
	elseif ($listJson.formatVersion -ne 1) { Fail "List JSON: formatVersion is $($listJson.formatVersion), expected 1" }
	elseif ($listText[-1] -ne $listCount) { Fail "List JSON: '$listCount' differs from the text output's '$($listText[-1])'" }
	elseif ($jsonAdd.Count -ne 1 -or $jsonAdd[0].visibility -ne 'public' -or $jsonAdd[0].static -or $jsonAdd[0].token -notmatch '^0x06[0-9A-F]{6}$') { Fail 'List JSON: Calculator.Add is missing or wrong' }
	elseif ($jsonOldHelperName.Count -ne 1 -or -not $jsonOldHelperName[0].static) { Fail 'List JSON: the static property OldHelper.Name is missing or not static' }
	elseif (-not @($listTypes | Where-Object { $_.compilerGenerated }).Count -or @($listTypes | Where-Object { $_.fullName -eq 'DiffCases.Unchanged' -and $_.compilerGenerated }).Count) { Fail 'List JSON: compilerGenerated is wrong' }
	else { Write-Host "  OK: $($listTypes.Count) types and $($listMembers.Count) members, the same in text and JSON, visibility and static" }

	# --protection: ../ProtectionCases writes an assembly with an obfuscator's marker, SuppressIldasm, unreadable names,
	# a Costura resource and an Assembly.Load(byte[]) call; DiffCases has none of them
	Write-Host "== Obfuscator/packer detection"
	$cleanText = @(Invoke-Checked "protection of DiffCases" { & $Console --protection $diffFile1 })
	if ($cleanText[-1] -ne '// No protection found') { Fail "Protection: DiffCases isn't clean: '$($cleanText[-1])'"; $cleanText | ForEach-Object { Write-Host "  $_" } }
	elseif (-not $debugInfoCheck) { Write-Host '  OK: DiffCases is clean (the protected test assembly needs a .NET build of dnSpy)' }
	else {
		$protectionDir = Join-Path $work 'ProtectionCases'
		$protectedFile = Join-Path $work 'ProtectionCases.dll'
		Invoke-Checked "build ProtectionCases" { dotnet build (Join-Path $PSScriptRoot '..\ProtectionCases\ProtectionCases.csproj') -c Release -o $protectionDir -v:q -nologo "-p:DnSpyBin=$dllDir" } > $null
		Invoke-Checked "run ProtectionCases" { dotnet (Join-Path $protectionDir 'ProtectionCases.dll') $dllDir $protectedFile } > $null
		$protectedText = @(Invoke-Checked "protection of ProtectionCases" { & $Console --protection $protectedFile })
		$protectedJson = (@(Invoke-Checked "protection of ProtectionCases as JSON" { & $Console --protection --json $protectedFile }) -join "`n") | ConvertFrom-Json
		$kinds = @($protectedJson.modules[0].findings | ForEach-Object { "$($_.kind):$($_.name)" })
		$expectedKinds = @('obfuscator:ConfuserEx / Confuser', 'antiDisassembly:SuppressIldasm', 'obfuscatedNames:Obfuscated names', 'embeddedAssemblies:Costura.Fody', 'hint:Loads assemblies from bytes')
		$missing = @($expectedKinds | Where-Object { $kinds -notcontains $_ })
		$extra = @($kinds | Where-Object { $expectedKinds -notcontains $_ })
		if ($protectedText[-1] -ne '// protected') { Fail "Protection: ProtectionCases isn't reported as protected: '$($protectedText[-1])'"; $protectedText | ForEach-Object { Write-Host "  $_" } }
		elseif ($missing.Count -or $extra.Count) { Fail "Protection JSON: missing [$($missing -join ', ')], not expected [$($extra -join ', ')]" }
		elseif ($protectedJson.formatVersion -ne 1 -or -not $protectedJson.modules[0].protected -or $protectedJson.modules[0].unreadableNames -ne 40) { Fail "Protection JSON: formatVersion/protected/unreadableNames ($($protectedJson.modules[0].unreadableNames), expected 40) wrong" }
		elseif (@($protectedText | Where-Object { $_ -like 'found: *' -or $_ -like 'hint: *' }).Count -ne $kinds.Count) { Fail 'Protection: the text and JSON output have a different number of findings' }
		else { Write-Host "  OK: DiffCases is clean; ProtectionCases: $($kinds.Count) findings, the same in text and JSON" }
	}

	# @file (response file) and --batch file (one command line per line, the other options added before each line)
	Write-Host "== Response files and batch files"
	$responseFile = Join-Path $work 'list.rsp'
	$batchFile = Join-Path $work 'jobs.txt'
	Set-Content -Path $responseFile -Encoding UTF8 -Value @('# list the types and members', "--list `"$diffFile1`"")
	Set-Content -Path $batchFile -Encoding UTF8 -Value @(
		'# comment, then an empty line', '',
		"@`"$responseFile`"",
		"--protection `"$diffFile1`"",
		'--batch nested.txt')
	$responseText = @(Invoke-Checked "list DiffCases from a response file" { & $Console "@$responseFile" })
	$batchText = @(& $Console --batch $batchFile --no-color 2>&1)
	$batchExit = $LASTEXITCODE
	$missingText = @(& $Console "@$(Join-Path $work 'missing.rsp')" 2>&1)
	$missingExit = $LASTEXITCODE
	$batchHeaders = @($batchText | Where-Object { $_ -like '// batch line *' })
	if (($responseText -join "`n") -ne ($listText -join "`n")) { Fail 'Response file: the output differs from --list on the command line'; $responseText | Select-Object -Last 5 | ForEach-Object { Write-Host "  $_" } }
	elseif ($batchExit -ne 1 -or $batchText[-1] -ne '// batch: 3 command line(s), 1 failed') { Fail "Batch: exit code $batchExit, last line '$($batchText[-1])', expected 1 and 3 command lines, 1 failed"; $batchText | Select-Object -Last 10 | ForEach-Object { Write-Host "  $_" } }
	elseif ($batchHeaders.Count -ne 3 -or $batchHeaders[0] -notlike '// batch line 3: @*' -or $batchHeaders[2] -ne '// batch line 5: --batch nested.txt') { Fail "Batch: wrong line headers: $($batchHeaders -join ' | ')" }
	elseif (-not ($batchText -contains $listText[-1]) -or -not ($batchText -contains '// No protection found')) { Fail 'Batch: the output of the list or protection line is missing' }
	elseif (-not ($batchText -contains "ERROR: --batch can't be used in a batch file")) { Fail 'Batch: a nested --batch was not reported' }
	elseif ($missingExit -ne 1 -or -not ($missingText -like 'ERROR: response file not found: *')) { Fail "Response file: a missing file wasn't reported (exit code $missingExit)" }
	else { Write-Host "  OK: response file output = command line output; batch ran 3 lines (comments and empty lines skipped), the failed one counted; missing response file reported" }
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
