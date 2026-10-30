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
using System.Xml.Linq;
using dnlib.DotNet;
using dnSpy.Decompiler.Diff;

namespace dnSpy.Decompiler.Patch {
	/// <summary>
	/// A changed method body in a patch. The method is found by the names that <see cref="AssemblyDiff"/> reports, so the
	/// patch can be applied to another build of the assembly (other tokens, added members).
	/// </summary>
	public sealed class MethodPatch {
		/// <summary>Full name of the type, eg. <c>NS.Outer/Inner</c></summary>
		public string TypeFullName { get; }
		/// <summary>Name and signature, eg. <c>System.Int32 Add(System.Int32,System.Int32)</c></summary>
		public string MethodName { get; }
		// The new body, see PatchBodyWriter
		internal XElement Body { get; }

		internal MethodPatch(string typeFullName, string methodName, XElement body) {
			TypeFullName = typeFullName;
			MethodName = methodName;
			Body = body;
		}

		public override string ToString() => TypeFullName + "::" + MethodName;
	}

	public sealed class PatchException : Exception {
		public PatchException(string message) : base(message) { }
	}

	/// <summary>
	/// The edits of an assembly (changed method bodies) that can be saved to a file and applied to another version of
	/// the assembly. The file is XML: types and members by name, instructions by opcode, branch targets by index.
	/// </summary>
	public sealed class AssemblyPatch {
		const string RootName = "dnSpyPatch";
		const int FormatVersion = 1;

		/// <summary>Full name of the assembly the patch was made from</summary>
		public string AssemblyName { get; }
		public List<MethodPatch> Methods { get; } = new List<MethodPatch>();

		AssemblyPatch(string assemblyName) => AssemblyName = assemblyName;

		/// <summary>
		/// Creates a patch with the method bodies that differ between <paramref name="original"/> and
		/// <paramref name="patched"/>. Other differences can't be in a patch yet and are returned in
		/// <paramref name="notIncluded"/>.
		/// </summary>
		public static AssemblyPatch Create(ModuleDef original, ModuleDef patched, out List<string> notIncluded) {
			var patch = new AssemblyPatch(patched.Assembly?.FullName ?? patched.Name.String);
			notIncluded = new List<string>();
			var diff = AssemblyDiff.Compare(original, patched);
			// Assembly references come and go with the code that uses them
			foreach (var change in diff.AssemblyChanges.Where(a => !a.StartsWith("+ reference ", StringComparison.Ordinal) && !a.StartsWith("- reference ", StringComparison.Ordinal)))
				notIncluded.Add("assembly " + change);
			foreach (var type in diff.Types) {
				if (type.Kind != DiffKind.Changed) {
					notIncluded.Add((type.Kind == DiffKind.Added ? "added type " : "removed type ") + type.FullName);
					continue;
				}
				foreach (var change in type.TypeChanges)
					notIncluded.Add($"type {type.FullName}: {change}");
				foreach (var member in type.Members) {
					var what = $"{member.MemberKind} {type.FullName}::{member.Name}";
					if (member.Kind != DiffKind.Changed) {
						notIncluded.Add((member.Kind == DiffKind.Added ? "added " : "removed ") + what);
						continue;
					}
					var details = member.Details!.Split(new[] { ", " }, StringSplitOptions.None);
					if (member.New is MethodDef method && details.Contains("body")) {
						var body = new PatchBodyWriter(patched).Write(method);
						patch.Methods.Add(new MethodPatch(type.FullName, member.Name, body));
					}
					foreach (var detail in details.Where(a => a != "body" || member.New is not MethodDef))
						notIncluded.Add($"{what}: {detail}");
				}
			}
			return patch;
		}

		/// <summary>
		/// Applies the patch to <paramref name="module"/>. Each method is applied or not; the methods that can't be
		/// applied (not found, or a type or member it uses is missing) are returned with the reason.
		/// </summary>
		public PatchApplyResult Apply(ModuleDef module) {
			var result = new PatchApplyResult();
			var reader = new PatchBodyReader(module);
			foreach (var methodPatch in Methods) {
				var method = AssemblyDiff.FindMethod(module, methodPatch.TypeFullName, methodPatch.MethodName);
				if (method is null) {
					result.Failed.Add((methodPatch, "method not found"));
					continue;
				}
				try {
					method.Body = reader.Read(method, methodPatch.Body);
					result.Applied.Add(methodPatch);
				}
				catch (PatchException ex) {
					result.Failed.Add((methodPatch, ex.Message));
				}
			}
			return result;
		}

		public void Save(Stream stream) {
			var root = new XElement(RootName,
				new XAttribute("version", FormatVersion.ToString(CultureInfo.InvariantCulture)),
				new XAttribute("assembly", AssemblyName));
			foreach (var method in Methods)
				root.Add(new XElement("method", new XAttribute("type", method.TypeFullName), new XAttribute("name", method.MethodName), method.Body));
			new XDocument(root).Save(stream);
		}

		public static AssemblyPatch Load(Stream stream) {
			XDocument doc;
			try {
				doc = XDocument.Load(stream);
			}
			catch (System.Xml.XmlException ex) {
				throw new PatchException("Not a dnSpy patch file: " + ex.Message);
			}
			var root = doc.Root;
			if (root?.Name != RootName)
				throw new PatchException("Not a dnSpy patch file");
			if (!int.TryParse((string?)root.Attribute("version"), NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version > FormatVersion)
				throw new PatchException("The patch file was made by a newer dnSpy");
			var patch = new AssemblyPatch((string?)root.Attribute("assembly") ?? string.Empty);
			foreach (var elem in root.Elements("method")) {
				var type = (string?)elem.Attribute("type");
				var name = (string?)elem.Attribute("name");
				var body = elem.Element("body");
				if (type is null || name is null || body is null)
					throw new PatchException("Invalid patch file: a method without type, name or body");
				patch.Methods.Add(new MethodPatch(type, name, body));
			}
			return patch;
		}
	}

	public sealed class PatchApplyResult {
		public List<MethodPatch> Applied { get; } = new List<MethodPatch>();
		public List<(MethodPatch method, string reason)> Failed { get; } = new List<(MethodPatch, string)>();
	}
}
