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

// The snapshot model only has strings, numbers and an opaque code location, so it can be
// shown after the process continued or exited, and tested without a debugged process.
namespace dnSpy.Debugger.History {
	/// <summary>
	/// Why the process stopped
	/// </summary>
	enum DbgHistoryStopReason {
		Other,
		Breakpoint,
		Step,
		Break,
		Exception,
		EntryPoint,
		ProgramBreak,
		SetIP,
	}

	/// <summary>
	/// Kind of a recorded value
	/// </summary>
	enum DbgHistoryValueKind {
		Local,
		Parameter,
		ReturnValue,
		Exception,
		Other,
	}

	/// <summary>
	/// A local, parameter, field, return value or exception, formatted when the process stopped
	/// </summary>
	sealed class DbgHistoryValue {
		public const int MaxStringLength = 1000;
		static readonly DbgHistoryValue[] noChildren = Array.Empty<DbgHistoryValue>();

		public DbgHistoryValueKind Kind { get; }
		public string Name { get; }
		public string Type { get; }
		public string Value { get; }

		/// <summary>
		/// true if the value couldn't be read (<see cref="Value"/> is the error message)
		/// </summary>
		public bool IsError { get; }

		/// <summary>
		/// true if the value has children (they're in <see cref="Children"/> unless the depth or value limit was reached)
		/// </summary>
		public bool HasChildren { get; }

		public IReadOnlyList<DbgHistoryValue> Children { get; }

		public DbgHistoryValue(DbgHistoryValueKind kind, string name, string type, string value, bool isError, bool hasChildren, IReadOnlyList<DbgHistoryValue>? children) {
			Kind = kind;
			Name = Truncate(name ?? throw new ArgumentNullException(nameof(name)));
			Type = Truncate(type ?? throw new ArgumentNullException(nameof(type)));
			Value = Truncate(value ?? throw new ArgumentNullException(nameof(value)));
			IsError = isError;
			HasChildren = hasChildren;
			Children = children ?? noChildren;
		}

		internal static string Truncate(string s) => s.Length <= MaxStringLength ? s : s.Substring(0, MaxStringLength) + "...";

		public override string ToString() => $"{Name} = {Value} ({Type})";
	}

	/// <summary>
	/// A frame of the recorded call stack
	/// </summary>
	sealed class DbgHistoryFrame {
		/// <summary>
		/// Frame index, 0 is the top frame
		/// </summary>
		public int Index { get; }

		/// <summary>
		/// Formatted frame, same as in the Call Stack window
		/// </summary>
		public string Name { get; }

		public string? ModuleName { get; }
		public uint FunctionToken { get; }
		public uint FunctionOffset { get; }

		/// <summary>
		/// A clone of the frame's DbgCodeLocation or null. Owned by the snapshot, the recorder
		/// closes it when the snapshot is removed from the history.
		/// </summary>
		public object? Location { get; }

		public DbgHistoryFrame(int index, string name, string? moduleName, uint functionToken, uint functionOffset, object? location) {
			Index = index;
			Name = DbgHistoryValue.Truncate(name ?? throw new ArgumentNullException(nameof(name)));
			ModuleName = moduleName;
			FunctionToken = functionToken;
			FunctionOffset = functionOffset;
			Location = location;
		}

		public override string ToString() => $"{Name} (0x{FunctionToken:X8}+IL_{FunctionOffset:X4})";
	}

	/// <summary>
	/// The state of the program at one stop
	/// </summary>
	sealed class DbgHistorySnapshot {
		static readonly DbgHistoryFrame[] noFrames = Array.Empty<DbgHistoryFrame>();
		static readonly DbgHistoryValue[] noValues = Array.Empty<DbgHistoryValue>();

		/// <summary>
		/// 1-based stop number in the debug session, set by <see cref="DbgHistory.Add(DbgHistorySnapshot)"/>
		/// </summary>
		public long Index { get; internal set; }

		public DateTime Time { get; }

		/// <summary>
		/// Time since the previous stop, set by <see cref="DbgHistory.Add(DbgHistorySnapshot)"/>
		/// </summary>
		public TimeSpan? Elapsed { get; internal set; }

		public DbgHistoryStopReason Reason { get; }
		public int ProcessId { get; }
		public string ProcessName { get; }
		public string RuntimeName { get; }
		public ulong ThreadId { get; }
		public string ThreadName { get; }

		/// <summary>
		/// Recorded frames, top frame first
		/// </summary>
		public IReadOnlyList<DbgHistoryFrame> Frames { get; }

		/// <summary>
		/// true if the call stack had more frames than were recorded
		/// </summary>
		public bool FramesTruncated { get; }

		/// <summary>
		/// Exceptions, return values, parameters and locals of the top frame with a location
		/// </summary>
		public IReadOnlyList<DbgHistoryValue> Values { get; }

		/// <summary>
		/// true if the time or value limit was reached and some values weren't recorded
		/// </summary>
		public bool IsPartial { get; }

		public DbgHistoryFrame? TopFrame => Frames.Count == 0 ? null : Frames[0];

		public DbgHistorySnapshot(DateTime time, DbgHistoryStopReason reason, int processId, string processName, string runtimeName, ulong threadId, string threadName, IReadOnlyList<DbgHistoryFrame>? frames, bool framesTruncated, IReadOnlyList<DbgHistoryValue>? values, bool isPartial) {
			Time = time;
			Reason = reason;
			ProcessId = processId;
			ProcessName = processName ?? string.Empty;
			RuntimeName = runtimeName ?? string.Empty;
			ThreadId = threadId;
			ThreadName = threadName ?? string.Empty;
			Frames = frames ?? noFrames;
			FramesTruncated = framesTruncated;
			Values = values ?? noValues;
			IsPartial = isPartial;
		}

		/// <summary>
		/// Number of recorded values, children included
		/// </summary>
		public int GetValueCount() => GetValueCount(Values);

		static int GetValueCount(IReadOnlyList<DbgHistoryValue> values) {
			int count = values.Count;
			foreach (var value in values)
				count += GetValueCount(value.Children);
			return count;
		}

		public override string ToString() => $"#{Index} {Reason} {TopFrame?.Name}";
	}
}
