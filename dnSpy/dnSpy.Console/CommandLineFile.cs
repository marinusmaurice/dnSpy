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
using System.Collections.Generic;
using System.IO;
using System.Text;
using dnSpy.Console.Properties;

namespace dnSpy_Console {
	/// <summary>
	/// Response files (<c>@file</c>) and batch files (<c>--batch file</c>): command lines read from text files
	/// </summary>
	static class CommandLineFile {
		public const string BatchOption = "--batch";
		const int MaxResponseFileDepth = 10;

		/// <summary>
		/// Splits a line into arguments. Spaces and tabs separate them, double quotes group them
		/// (<c>""</c> in a quoted argument is one quote), backslashes are kept as they are (paths)
		/// </summary>
		public static List<string> Split(string line) {
			var args = new List<string>();
			var sb = new StringBuilder();
			bool inQuotes = false, hasArg = false;
			for (int i = 0; i < line.Length; i++) {
				char c = line[i];
				if (c == '"') {
					hasArg = true;
					if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') {
						sb.Append('"');
						i++;
					}
					else
						inQuotes = !inQuotes;
				}
				else if (!inQuotes && (c == ' ' || c == '\t')) {
					if (hasArg) {
						args.Add(sb.ToString());
						sb.Clear();
						hasArg = false;
					}
				}
				else {
					sb.Append(c);
					hasArg = true;
				}
			}
			if (hasArg)
				args.Add(sb.ToString());
			return args;
		}

		/// <summary>
		/// Returns the command lines of a file: one per line, empty lines and lines starting with <c>#</c> are skipped
		/// </summary>
		public static List<(int lineNumber, string line)> ReadLines(string filename) {
			var result = new List<(int, string)>();
			var lines = File.ReadAllLines(filename);
			for (int i = 0; i < lines.Length; i++) {
				var line = lines[i].Trim();
				if (line.Length == 0 || line[0] == '#')
					continue;
				result.Add((i + 1, line));
			}
			return result;
		}

		/// <summary>
		/// Replaces each <c>@file</c> argument with the arguments in that file (nested response files too).
		/// Arguments after <c>--</c> are file names and aren't expanded
		/// </summary>
		public static string[] ExpandResponseFiles(string[] args) {
			bool any = false;
			foreach (var arg in args) {
				if (arg == "--")
					break;
				if (arg.Length > 1 && arg[0] == '@') {
					any = true;
					break;
				}
			}
			if (!any)
				return args;
			var result = new List<string>();
			Expand(args, result, 0, out _);
			return result.ToArray();
		}

		static void Expand(IEnumerable<string> args, List<string> result, int depth, out bool sawFileSeparator) {
			sawFileSeparator = false;
			foreach (var arg in args) {
				if (sawFileSeparator || arg.Length <= 1 || arg[0] != '@') {
					if (arg == "--")
						sawFileSeparator = true;
					result.Add(arg);
					continue;
				}
				var filename = arg.Substring(1);
				if (depth >= MaxResponseFileDepth)
					throw new ErrorException(string.Format(dnSpy_Console_Resources.ResponseFileTooDeep, filename));
				if (!File.Exists(filename))
					throw new ErrorException(string.Format(dnSpy_Console_Resources.ResponseFileNotFound, filename));
				var fileArgs = new List<string>();
				foreach (var (_, line) in ReadLines(filename))
					fileArgs.AddRange(Split(line));
				Expand(fileArgs, result, depth + 1, out sawFileSeparator);
			}
		}

		/// <summary>
		/// Finds <c>--batch file</c> (before <c>--</c>); <paramref name="sharedArgs"/> are the other arguments, used for every line
		/// </summary>
		public static bool TryGetBatchFile(string[] args, out string? batchFile, out string[] sharedArgs) {
			for (int i = 0; i < args.Length; i++) {
				if (args[i] == "--")
					break;
				if (args[i] != BatchOption)
					continue;
				if (i + 1 >= args.Length)
					throw new ErrorException(dnSpy_Console_Resources.BatchNeedsFile);
				batchFile = args[i + 1];
				var list = new List<string>(args.Length - 2);
				for (int j = 0; j < args.Length; j++) {
					if (j != i && j != i + 1)
						list.Add(args[j]);
				}
				sharedArgs = list.ToArray();
				return true;
			}
			batchFile = null;
			sharedArgs = args;
			return false;
		}

		/// <summary>
		/// Runs each command line in a batch file, after <paramref name="sharedArgs"/>; returns 1 when a line failed
		/// </summary>
		public static int RunBatch(string batchFile, string[] sharedArgs, Func<string[], int> run) {
			if (!File.Exists(batchFile))
				throw new ErrorException(string.Format(dnSpy_Console_Resources.BatchFileNotFound, batchFile));
			var lines = ReadLines(batchFile);
			int failed = 0;
			foreach (var (lineNumber, line) in lines) {
				Console.WriteLine(dnSpy_Console_Resources.BatchLine, lineNumber, line);
				int exitCode;
				try {
					var lineArgs = new List<string>(sharedArgs);
					lineArgs.AddRange(ExpandResponseFiles(Split(line).ToArray()));
					if (TryGetBatchFile(lineArgs.ToArray(), out _, out _))
						throw new ErrorException(dnSpy_Console_Resources.BatchInBatch);
					exitCode = run(lineArgs.ToArray());
				}
				catch (ErrorException ex) {
					Console.WriteLine(dnSpy_Console_Resources.Error1, ex.Message);
					exitCode = 1;
				}
				if (exitCode != 0)
					failed++;
			}
			Console.WriteLine(dnSpy_Console_Resources.BatchSummary, lines.Count, failed);
			return failed == 0 ? 0 : 1;
		}
	}
}
