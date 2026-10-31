/*
    Copyright (C) 2014-2019 de4dot@gmail.com
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
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.Decompiler.Diff {
	/// <summary>
	/// Writes an <see cref="AssemblyDiffResult"/>: the summary (dnSpy's diff node, <c>dnSpy.Console --diff</c>) and the
	/// code diff of a type. Members are references, so they can be clicked in dnSpy.
	/// </summary>
	public static class AssemblyDiffWriter {
		public static void WriteSummary(IDecompilerOutput output, AssemblyDiffResult result) {
			WriteComment(output, "Old: " + GetName(result.OldModule));
			WriteComment(output, "New: " + GetName(result.NewModule));
			WriteComment(output, $"Types: {result.Count(DiffKind.Added)} added, {result.Count(DiffKind.Removed)} removed, {result.Count(DiffKind.Changed)} changed{Renamed(result.Count(DiffKind.Renamed))}");
			if (result.Resources.Count != 0)
				WriteComment(output, $"Resources: {CountResources(result, DiffKind.Added)} added, {CountResources(result, DiffKind.Removed)} removed, {CountResources(result, DiffKind.Changed)} changed");
			foreach (var change in result.AssemblyChanges)
				WriteComment(output, "Assembly " + change);
			output.WriteLine();
			if (result.Types.Count == 0 && result.AssemblyChanges.Count == 0 && result.Resources.Count == 0) {
				WriteComment(output, "No differences");
				return;
			}

			foreach (var type in result.Types) {
				WriteKind(output, type.Kind);
				output.Write("type", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				output.Write(type.FullName, (object?)type.New ?? type.Old, DecompilerReferenceFlags.None, BoxedTextColor.Type);
				if (type.OldFullName is not null)
					WriteRenamedFrom(output, type.OldFullName);
				output.WriteLine();
				foreach (var change in type.TypeChanges) {
					output.Write("    ", BoxedTextColor.Text);
					output.Write(change, BoxedTextColor.Comment);
					output.WriteLine();
				}
				foreach (var member in type.Members) {
					output.Write("    ", BoxedTextColor.Text);
					WriteKind(output, member.Kind);
					output.Write(member.MemberKind, BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write(member.Name, (object?)member.New ?? member.Old, DecompilerReferenceFlags.None, GetColor(member));
					if (member.Details is not null) {
						output.Write("  ", BoxedTextColor.Text);
						output.Write("// " + member.Details, BoxedTextColor.Comment);
					}
					if (member.OldName is not null)
						WriteRenamedFrom(output, member.OldName);
					output.WriteLine();
				}
			}

			foreach (var resource in result.Resources) {
				WriteKind(output, resource.Kind);
				output.Write("resource", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				output.Write(resource.Name, BoxedTextColor.String);
				if (resource.Details is not null) {
					output.Write("  ", BoxedTextColor.Text);
					output.Write("// " + resource.Details, BoxedTextColor.Comment);
				}
				output.WriteLine();
				foreach (var entry in resource.Entries) {
					output.Write("    " + entry, BoxedTextColor.Text);
					output.WriteLine();
				}
			}
		}

		static string Renamed(int count) => count == 0 ? string.Empty : $", {count} renamed";

		static int CountResources(AssemblyDiffResult result, DiffKind kind) => result.Resources.Count(a => a.Kind == kind);

		static void WriteRenamedFrom(IDecompilerOutput output, string oldName) {
			output.Write("  ", BoxedTextColor.Text);
			output.Write("// renamed from " + oldName, BoxedTextColor.Comment);
		}

		/// <summary>
		/// Writes the line diff of the old and new decompiled code of a type (or all its code if it was added or removed)
		/// </summary>
		public static void WriteCodeDiff(IDecompilerOutput output, IDecompiler decompiler, TypeDiff type, CancellationToken cancellationToken) {
			var lines = GetCodeDiff(decompiler, type, cancellationToken);
			WriteComment(output, type.OldFullName is null ? $"{type.Kind}: {type.FullName}" : $"{type.Kind}: {type.OldFullName} -> {type.FullName}");
			WriteLines(output, lines);
		}

		/// <summary>
		/// Gets the line diff of the old and new decompiled code of a type (or all its code if it was added or removed)
		/// </summary>
		public static List<DiffLine> GetCodeDiff(IDecompiler decompiler, TypeDiff type, CancellationToken cancellationToken) {
			var oldLines = type.Old is null ? Array.Empty<string>() : TextDiff.GetCodeLines(Decompile(decompiler, type.Old, cancellationToken));
			var newLines = type.New is null ? Array.Empty<string>() : TextDiff.GetCodeLines(Decompile(decompiler, type.New, cancellationToken));
			return TextDiff.Diff(oldLines, newLines);
		}

		public static void WriteLines(IDecompilerOutput output, List<DiffLine> lines) {
			foreach (var line in lines) {
				switch (line.Kind) {
				case DiffLineKind.Same:
					output.Write("  " + line.Text, BoxedTextColor.Text);
					break;
				case DiffLineKind.Added:
					output.Write("+ " + line.Text, BoxedTextColor.Green);
					break;
				case DiffLineKind.Removed:
					output.Write("- " + line.Text, BoxedTextColor.Red);
					break;
				case DiffLineKind.Skipped:
					output.Write("  ...", BoxedTextColor.Comment);
					break;
				}
				output.WriteLine();
			}
		}

		static string Decompile(IDecompiler decompiler, TypeDef type, CancellationToken cancellationToken) {
			var output = new StringBuilderDecompilerOutput();
			decompiler.Decompile(type, output, new DecompilationContext { CancellationToken = cancellationToken });
			return output.ToString();
		}

		static string GetName(ModuleDef module) {
			var name = module.Assembly?.FullName ?? module.Name.String;
			return string.IsNullOrEmpty(module.Location) ? name : name + " (" + module.Location + ")";
		}

		static void WriteComment(IDecompilerOutput output, string text) {
			output.Write("// " + text, BoxedTextColor.Comment);
			output.WriteLine();
		}

		static void WriteKind(IDecompilerOutput output, DiffKind kind) {
			switch (kind) {
			case DiffKind.Added: output.Write("+ ", BoxedTextColor.Green); break;
			case DiffKind.Removed: output.Write("- ", BoxedTextColor.Red); break;
			default: output.Write("~ ", BoxedTextColor.Text); break;
			}
		}

		static object GetColor(MemberDiff member) => member.MemberKind switch {
			"method" => BoxedTextColor.InstanceMethod,
			"field" => BoxedTextColor.InstanceField,
			"property" => BoxedTextColor.InstanceProperty,
			"event" => BoxedTextColor.InstanceEvent,
			_ => BoxedTextColor.Text,
		};
	}
}
