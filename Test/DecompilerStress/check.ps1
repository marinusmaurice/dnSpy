<#
	Decompiler regression test for C# (ILSpy 11), run by CI and by hand:

		./Test/DecompilerStress/check.ps1 [-Console <path to dnSpy.Console.exe>]

	Builds DecompilerStress in Debug and Release, decompiles Program with dnSpy.Console (default language), and checks:
	  - no compiler names are left: <>h__TransparentIdentifier, if (1 == 0)
	  - every `let` clause is restored (sections 4 and 11)
	  - the decompiled code compiles and prints the same as the original (HashCode.Combine values are random per process)
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
