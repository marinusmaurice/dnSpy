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
using System.Globalization;
using System.IO;
using System.Linq;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;

// The Heap window's Save Snapshot / Open Snapshot: a snapshot's types (count, size, retained size) in a text file,
// to compare a later debugging session with. The objects' addresses aren't saved: they mean nothing in another
// process, so a saved snapshot is compared by type (Count +/-, Size +/-), not by object (New).
// Test/HeapSnapshotCheck links this file.
namespace dnSpy.Debugger.DotNet.Heap {
	sealed class HeapSnapshotFileType {
		public string TypeName { get; }
		public long Count { get; }
		public long TotalSize { get; }
		/// <summary>-1 if unknown</summary>
		public long RetainedSize { get; }
		public HeapSnapshotFileType(string typeName, long count, long totalSize, long retainedSize) {
			TypeName = typeName;
			Count = count;
			TotalSize = totalSize;
			RetainedSize = retainedSize;
		}
	}

	sealed class HeapSnapshotFile {
		public const string Extension = "dnheap";
		const string Header = "dnSpy heap snapshot";
		const int FormatVersion = 1;

		/// <summary>The debugged program's name, eg. "MyApp.exe", or empty</summary>
		public string ProcessName { get; }
		/// <summary>When the snapshot was taken (UTC)</summary>
		public DateTime Time { get; }
		public List<HeapSnapshotFileType> Types { get; }

		public HeapSnapshotFile(string processName, DateTime time, List<HeapSnapshotFileType> types) {
			ProcessName = processName;
			Time = time;
			Types = types;
		}

		public static HeapSnapshotFile Create(DbgDotNetHeapSnapshot snapshot, string processName, DateTime time) =>
			new HeapSnapshotFile(processName, time, snapshot.Types.Select(a => new HeapSnapshotFileType(a.TypeName, a.Count, a.TotalSize, a.RetainedSize)).ToList());

		/// <summary>
		/// Text, one line per type: count, size, retained size (-1 if unknown) and the type name, separated by tabs
		/// </summary>
		public void Write(TextWriter writer) {
			writer.Write(Header + " " + FormatVersion.ToString(CultureInfo.InvariantCulture) + "\n");
			writer.Write("process\t" + Clean(ProcessName) + "\n");
			writer.Write("time\t" + Time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\n");
			writer.Write("types\t" + Types.Count.ToString(CultureInfo.InvariantCulture) + "\n");
			foreach (var t in Types) {
				writer.Write(t.Count.ToString(CultureInfo.InvariantCulture));
				writer.Write('\t');
				writer.Write(t.TotalSize.ToString(CultureInfo.InvariantCulture));
				writer.Write('\t');
				writer.Write(t.RetainedSize.ToString(CultureInfo.InvariantCulture));
				writer.Write('\t');
				writer.Write(Clean(t.TypeName));
				writer.Write('\n');
			}
		}

		// A type name can't have a tab or a new line, but don't let one break the file
		static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

		/// <summary>Reads a file written by <see cref="Write"/>; throws <see cref="InvalidDataException"/> if it isn't one</summary>
		public static HeapSnapshotFile Read(TextReader reader) {
			var first = reader.ReadLine();
			if (first is null || !first.StartsWith(Header + " ", StringComparison.Ordinal))
				throw new InvalidDataException("Not a dnSpy heap snapshot");
			if (!int.TryParse(first.Substring(Header.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version < 1)
				throw new InvalidDataException("Not a dnSpy heap snapshot");
			if (version > FormatVersion)
				throw new InvalidDataException("This heap snapshot was saved by a newer dnSpy (format " + version.ToString(CultureInfo.InvariantCulture) + ")");
			var processName = string.Empty;
			var time = DateTime.MinValue;
			int typeCount = -1;
			string? line;
			while (typeCount < 0 && (line = reader.ReadLine()) is not null) {
				int tab = line.IndexOf('\t');
				if (tab < 0)
					throw new InvalidDataException("Bad line: " + line);
				var key = line.Substring(0, tab);
				var value = line.Substring(tab + 1);
				switch (key) {
				case "process":
					processName = value;
					break;
				case "time":
					if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out time))
						throw new InvalidDataException("Bad time: " + value);
					break;
				case "types":
					if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out typeCount))
						throw new InvalidDataException("Bad type count: " + value);
					break;
				default:
					// A later format's extra header lines
					break;
				}
			}
			if (typeCount < 0)
				throw new InvalidDataException("The file has no types");
			var types = new List<HeapSnapshotFileType>(Math.Min(typeCount, 100_000));
			for (int i = 0; i < typeCount; i++) {
				line = reader.ReadLine();
				if (line is null)
					throw new InvalidDataException("The file ends too early");
				var parts = line.Split(new[] { '\t' }, 4);
				if (parts.Length != 4 ||
					!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long count) ||
					!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
					!long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long retained) || retained < -1)
					throw new InvalidDataException("Bad line: " + line);
				types.Add(new HeapSnapshotFileType(parts[3], count, size, retained));
			}
			return new HeapSnapshotFile(processName, time, types);
		}
	}
}
