/*
    Copyright (C) 2026- marinusmaurice@outlook.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Globalization;
using System.IO;
using System.Text;

// Updating a dnSpy in a folder that only an administrator can change (eg. Program Files). The downloaded zip is in
// %TEMP%, which any program the user runs can change, so the elevated part trusts nothing there: the script is
// passed on the command line (-EncodedCommand), not in a file, and it copies the zip into dnSpy's own folder before
// it checks the zip's SHA-256 and unpacks it, so the files it copies are the ones it checked. dnSpy is started again
// through explorer.exe so it doesn't run as administrator. Test/UpdateCheck runs the script without elevation.
namespace dnSpy.MainApp.Update {
	static class ElevatedUpdate {
		/// <summary>Windows PowerShell 5.1, which every supported Windows has (not found through PATH)</summary>
		public static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

		/// <summary>
		/// The PowerShell script: waits up to a minute for dnSpy (<paramref name="processId"/>) to exit, copies
		/// <paramref name="zipFile"/> into a new folder in <paramref name="installDirectory"/>, checks its SHA-256
		/// (<paramref name="sha256"/>, hex), unpacks it and copies the files over the install directory, then starts
		/// <paramref name="restartExe"/> (if not null) through explorer.exe. Exit code 0 if the files were copied.
		/// The process is identified by its id and start time (<paramref name="processStartTime"/>): Windows can give
		/// the id to a new process once dnSpy has exited.
		/// </summary>
		public static string CreateScript(int processId, DateTime processStartTime, string zipFile, string sha256, string installDirectory, string? restartExe) {
			if (sha256.Length != 64 || !IsHex(sha256))
				throw new ArgumentException("Not a SHA-256 hash: " + sha256, nameof(sha256));
			var sb = new StringBuilder();
			sb.AppendLine("$ErrorActionPreference = 'Stop'");
			sb.AppendLine("$result = 1");
			sb.AppendLine("function Test-Running { $p = Get-Process -Id " + processId.ToString(CultureInfo.InvariantCulture) + " -ErrorAction SilentlyContinue; [bool]($p -and $p.StartTime.ToFileTimeUtc() -eq " + processStartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) + ") }");
			sb.AppendLine("for ($i = 0; $i -lt 60 -and (Test-Running); $i++) { Start-Sleep -Seconds 1 }");
			sb.AppendLine("if (-not (Test-Running)) {");
			sb.AppendLine("  $dir = " + Quote(installDirectory));
			sb.AppendLine("  $work = Join-Path $dir ('.dnSpy-update-' + [guid]::NewGuid().ToString('N'))");
			sb.AppendLine("  try {");
			sb.AppendLine("    New-Item -ItemType Directory -Path $work | Out-Null");
			sb.AppendLine("    $zip = Join-Path $work 'update.zip'");
			sb.AppendLine("    Copy-Item -LiteralPath " + Quote(zipFile) + " -Destination $zip");
			sb.AppendLine("    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash");
			sb.AppendLine("    if ($hash -ne '" + sha256.ToUpperInvariant() + "') { throw 'The update file was changed after it was downloaded' }");
			sb.AppendLine("    Add-Type -AssemblyName System.IO.Compression.FileSystem");
			sb.AppendLine("    $files = Join-Path $work 'files'");
			sb.AppendLine("    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $files)");
			sb.AppendLine("    & robocopy.exe $files $dir /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null");
			// robocopy: 0-7 = success
			sb.AppendLine("    if ($LASTEXITCODE -lt 8) { $result = 0 }");
			sb.AppendLine("  }");
			sb.AppendLine("  catch { }");
			sb.AppendLine("  finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }");
			if (restartExe is not null)
				sb.AppendLine("  Start-Process -FilePath (Join-Path $env:SystemRoot 'explorer.exe') -ArgumentList ('\"' + " + Quote(restartExe) + " + '\"')");
			sb.AppendLine("}");
			sb.AppendLine("exit $result");
			return sb.ToString();
		}

		/// <summary>The arguments for powershell.exe that run <paramref name="script"/></summary>
		public static string CreateArguments(string script) =>
			"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

		// A PowerShell single-quoted string: nothing in it is expanded; ' is written ''
		static string Quote(string s) {
			var sb = new StringBuilder(s.Length + 2);
			sb.Append('\'');
			foreach (var c in s) {
				// PowerShell also ends a single-quoted string at the typographic single quotes
				if (c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛')
					sb.Append(c);
				sb.Append(c);
			}
			return sb.Append('\'').ToString();
		}

		static bool IsHex(string s) {
			foreach (var c in s) {
				if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
					return false;
			}
			return true;
		}
	}
}
