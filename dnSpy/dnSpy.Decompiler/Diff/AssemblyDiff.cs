/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.Diff {
	public enum DiffKind {
		Added,
		Removed,
		Changed,
	}

	/// <summary>
	/// An added, removed or changed member of a type that's in both versions
	/// </summary>
	public sealed class MemberDiff {
		public DiffKind Kind { get; }
		/// <summary>The member in the old version (null if it was added)</summary>
		public IMemberDef? Old { get; }
		/// <summary>The member in the new version (null if it was removed)</summary>
		public IMemberDef? New { get; }
		/// <summary>"method", "field", "property", "event"</summary>
		public string MemberKind { get; }
		/// <summary>Name and signature, eg. <c>System.Int32 Add(System.Int32,System.Int32)</c></summary>
		public string Name { get; }
		/// <summary>What changed, eg. "body", "attributes"</summary>
		public string? Details { get; }

		public MemberDiff(DiffKind kind, IMemberDef? old, IMemberDef? @new, string memberKind, string name, string? details) {
			Kind = kind;
			Old = old;
			New = @new;
			MemberKind = memberKind;
			Name = name;
			Details = details;
		}
	}

	/// <summary>
	/// An added, removed or changed type. Compiler-generated nested types (lambdas, iterators, async methods) are part
	/// of the type that declares them.
	/// </summary>
	public sealed class TypeDiff {
		public DiffKind Kind { get; }
		public TypeDef? Old { get; }
		public TypeDef? New { get; }
		public string FullName { get; }
		/// <summary>Changes of the type itself (base type, interfaces, attributes)</summary>
		public List<string> TypeChanges { get; } = new List<string>();
		public List<MemberDiff> Members { get; } = new List<MemberDiff>();

		public TypeDiff(DiffKind kind, TypeDef? old, TypeDef? @new, string fullName) {
			Kind = kind;
			Old = old;
			New = @new;
			FullName = fullName;
		}
	}

	public sealed class AssemblyDiffResult {
		public ModuleDef OldModule { get; }
		public ModuleDef NewModule { get; }
		public List<TypeDiff> Types { get; } = new List<TypeDiff>();
		/// <summary>Changes of the assembly itself (version, references)</summary>
		public List<string> AssemblyChanges { get; } = new List<string>();

		public AssemblyDiffResult(ModuleDef oldModule, ModuleDef newModule) {
			OldModule = oldModule;
			NewModule = newModule;
		}

		public int Count(DiffKind kind) => Types.Count(a => a.Kind == kind);
	}

	/// <summary>
	/// Compares two versions of an assembly: types and members are matched by name and signature, and method bodies
	/// by their IL (operands by name, not token, so a rebuilt assembly with the same code has no changes)
	/// </summary>
	public static class AssemblyDiff {
		public static AssemblyDiffResult Compare(ModuleDef oldModule, ModuleDef newModule) {
			var result = new AssemblyDiffResult(oldModule, newModule);
			CompareAssemblies(result);

			var oldTypes = GetTypes(oldModule);
			var newTypes = GetTypes(newModule);
			foreach (var name in oldTypes.Keys.Union(newTypes.Keys).OrderBy(a => a, StringComparer.Ordinal)) {
				oldTypes.TryGetValue(name, out var oldType);
				newTypes.TryGetValue(name, out var newType);
				if (oldType is null)
					result.Types.Add(new TypeDiff(DiffKind.Added, null, newType, name));
				else if (newType is null)
					result.Types.Add(new TypeDiff(DiffKind.Removed, oldType, null, name));
				else {
					var diff = new TypeDiff(DiffKind.Changed, oldType, newType, name);
					CompareTypes(diff, oldType, newType);
					if (diff.TypeChanges.Count != 0 || diff.Members.Count != 0)
						result.Types.Add(diff);
				}
			}
			return result;
		}

		static void CompareAssemblies(AssemblyDiffResult result) {
			var oldAsm = result.OldModule.Assembly;
			var newAsm = result.NewModule.Assembly;
			if (oldAsm is not null && newAsm is not null && oldAsm.Version != newAsm.Version)
				result.AssemblyChanges.Add($"version: {oldAsm.Version} -> {newAsm.Version}");
			var oldRefs = new HashSet<string>(result.OldModule.GetAssemblyRefs().Select(a => a.FullName), StringComparer.Ordinal);
			var newRefs = new HashSet<string>(result.NewModule.GetAssemblyRefs().Select(a => a.FullName), StringComparer.Ordinal);
			foreach (var r in newRefs.Except(oldRefs).OrderBy(a => a, StringComparer.Ordinal))
				result.AssemblyChanges.Add("+ reference " + r);
			foreach (var r in oldRefs.Except(newRefs).OrderBy(a => a, StringComparer.Ordinal))
				result.AssemblyChanges.Add("- reference " + r);
		}

		// The types to compare, by full name. Compiler-generated nested types are compared with the type that declares
		// them; top-level compiler-generated types (<PrivateImplementationDetails>, <>f__AnonymousType0) are skipped.
		static Dictionary<string, TypeDef> GetTypes(ModuleDef module) {
			var dict = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
			foreach (var type in module.GetTypes()) {
				if (IsCompilerGenerated(type))
					continue;
				dict[type.FullName] = type;
			}
			return dict;
		}

		static bool IsCompilerGenerated(TypeDef type) => type.Name.String.IndexOf('<') >= 0 && !type.IsGlobalModuleType;

		static void CompareTypes(TypeDiff diff, TypeDef oldType, TypeDef newType) {
			if (oldType.Attributes != newType.Attributes)
				diff.TypeChanges.Add($"attributes: {oldType.Attributes} -> {newType.Attributes}");
			var oldBase = oldType.BaseType?.FullName ?? string.Empty;
			var newBase = newType.BaseType?.FullName ?? string.Empty;
			if (oldBase != newBase)
				diff.TypeChanges.Add($"base type: {Or(oldBase)} -> {Or(newBase)}");
			var oldIfaces = new HashSet<string>(oldType.Interfaces.Select(a => a.Interface?.FullName ?? string.Empty), StringComparer.Ordinal);
			var newIfaces = new HashSet<string>(newType.Interfaces.Select(a => a.Interface?.FullName ?? string.Empty), StringComparer.Ordinal);
			foreach (var i in newIfaces.Except(oldIfaces).OrderBy(a => a, StringComparer.Ordinal))
				diff.TypeChanges.Add("+ interface " + i);
			foreach (var i in oldIfaces.Except(newIfaces).OrderBy(a => a, StringComparer.Ordinal))
				diff.TypeChanges.Add("- interface " + i);
			if (oldType.GenericParameters.Count != newType.GenericParameters.Count)
				diff.TypeChanges.Add("generic parameters");
			if (GetCustomAttributes(oldType) != GetCustomAttributes(newType))
				diff.TypeChanges.Add("custom attributes");

			var oldMembers = GetMembers(oldType);
			var newMembers = GetMembers(newType);
			foreach (var key in oldMembers.Keys.Union(newMembers.Keys).OrderBy(a => a, StringComparer.Ordinal)) {
				oldMembers.TryGetValue(key, out var oldMember);
				newMembers.TryGetValue(key, out var newMember);
				var info = oldMember.member is not null ? oldMember : newMember;
				if (oldMember.member is null)
					diff.Members.Add(new MemberDiff(DiffKind.Added, null, newMember.member, info.kind, info.name, null));
				else if (newMember.member is null)
					diff.Members.Add(new MemberDiff(DiffKind.Removed, oldMember.member, null, info.kind, info.name, null));
				else {
					var details = CompareMembers(oldMember.member, newMember.member);
					if (details is not null)
						diff.Members.Add(new MemberDiff(DiffKind.Changed, oldMember.member, newMember.member, info.kind, info.name, details));
				}
			}

			// An added or removed property/event implies its accessors, an auto property its backing field
			var accessors = new HashSet<MethodDef>();
			foreach (var member in diff.Members) {
				if (member.Kind == DiffKind.Changed)
					continue;
				if ((member.Old ?? member.New) is PropertyDef prop)
					accessors.UnionWith(prop.GetMethods.Concat(prop.SetMethods).Concat(prop.OtherMethods));
				else if ((member.Old ?? member.New) is EventDef evt)
					accessors.UnionWith(new[] { evt.AddMethod, evt.RemoveMethod, evt.InvokeMethod }.Where(a => a is not null).Concat(evt.OtherMethods)!);
			}
			diff.Members.RemoveAll(a => a.Kind != DiffKind.Changed &&
				((a.Old ?? a.New) is MethodDef method && accessors.Contains(method) ||
				(a.Old ?? a.New) is FieldDef field && field.Name.String.IndexOf('<') >= 0));
		}

		static string Or(string s) => s.Length == 0 ? "(none)" : s;

		// Key: kind and signature. The members of compiler-generated nested types get the nested type's name as prefix.
		static Dictionary<string, (IMemberDef member, string kind, string name)> GetMembers(TypeDef type) {
			var dict = new Dictionary<string, (IMemberDef, string, string)>(StringComparer.Ordinal);
			AddMembers(dict, type, string.Empty);
			return dict;
		}

		static void AddMembers(Dictionary<string, (IMemberDef, string, string)> dict, TypeDef type, string prefix) {
			foreach (var m in type.Methods) {
				var name = prefix + FullNameFactory.MethodFullName(null, m.Name, m.MethodSig, null, null, null, null);
				if (m.MethodSig?.GenParamCount > 0)
					name += "`" + m.MethodSig.GenParamCount.ToString(CultureInfo.InvariantCulture);
				dict["M:" + name] = (m, "method", name);
			}
			foreach (var f in type.Fields) {
				var name = prefix + f.FieldType?.FullName + " " + f.Name;
				dict["F:" + name] = (f, "field", name);
			}
			foreach (var p in type.Properties) {
				var name = prefix + FullNameFactory.PropertyFullName(null, p.Name, p.Type, null, null);
				// Only indexers have parameters
				if (name.EndsWith("()", StringComparison.Ordinal))
					name = name.Substring(0, name.Length - 2);
				dict["P:" + name] = (p, "property", name);
			}
			foreach (var e in type.Events) {
				var name = prefix + e.EventType?.FullName + " " + e.Name;
				dict["E:" + name] = (e, "event", name);
			}
			foreach (var nested in type.NestedTypes) {
				if (IsCompilerGenerated(nested))
					AddMembers(dict, nested, prefix + nested.Name + "::");
			}
		}

		// Returns what changed or null if nothing changed
		static string? CompareMembers(IMemberDef oldMember, IMemberDef newMember) {
			var changes = new List<string>();
			switch (oldMember) {
			case MethodDef oldMethod:
				var newMethod = (MethodDef)newMember;
				if (oldMethod.Attributes != newMethod.Attributes || oldMethod.ImplAttributes != newMethod.ImplAttributes)
					changes.Add("attributes");
				if (GetBody(oldMethod) != GetBody(newMethod))
					changes.Add("body");
				if (GetParameterNames(oldMethod) != GetParameterNames(newMethod))
					changes.Add("parameter names");
				break;

			case FieldDef oldField:
				var newField = (FieldDef)newMember;
				if (oldField.Attributes != newField.Attributes)
					changes.Add("attributes");
				if (!Equals(oldField.Constant?.Value, newField.Constant?.Value))
					changes.Add("value");
				else if (!(oldField.InitialValue ?? Array.Empty<byte>()).SequenceEqual(newField.InitialValue ?? Array.Empty<byte>()))
					changes.Add("initial value");
				break;

			case PropertyDef oldProp:
				if (oldProp.Attributes != ((PropertyDef)newMember).Attributes)
					changes.Add("attributes");
				break;

			case EventDef oldEvent:
				if (oldEvent.Attributes != ((EventDef)newMember).Attributes)
					changes.Add("attributes");
				break;
			}
			if (oldMember is IHasCustomAttribute oldCa && newMember is IHasCustomAttribute newCa && GetCustomAttributes(oldCa) != GetCustomAttributes(newCa))
				changes.Add("custom attributes");
			return changes.Count == 0 ? null : string.Join(", ", changes);
		}

		static string GetParameterNames(MethodDef method) => string.Join(",", method.ParamDefs.Select(a => a.Name.String));

		static string GetCustomAttributes(IHasCustomAttribute member) {
			var sb = new StringBuilder();
			foreach (var ca in member.CustomAttributes.OrderBy(a => a.TypeFullName, StringComparer.Ordinal)) {
				sb.Append(ca.TypeFullName).Append('(');
				foreach (var arg in ca.ConstructorArguments)
					sb.Append(FormatArgument(arg)).Append(',');
				foreach (var arg in ca.NamedArguments)
					sb.Append(arg.Name).Append('=').Append(FormatArgument(arg.Argument)).Append(',');
				sb.Append(')');
			}
			return sb.ToString();
		}

		static string FormatArgument(CAArgument arg) => arg.Value switch {
			null => "null",
			IList<CAArgument> list => "[" + string.Join(",", list.Select(FormatArgument)) + "]",
			TypeSig sig => sig.FullName,
			IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
			var v => v.ToString() ?? string.Empty,
		};

		// The IL with names instead of tokens and instruction indexes instead of offsets
		static string GetBody(MethodDef method) {
			var body = method.Body;
			if (body is null)
				return method.HasBody ? "?" : string.Empty;
			var sb = new StringBuilder();
			var index = new Dictionary<Instruction, int>();
			for (int i = 0; i < body.Instructions.Count; i++)
				index[body.Instructions[i]] = i;
			foreach (var local in body.Variables)
				sb.Append(local.Type?.FullName).Append(';');
			sb.Append(body.InitLocals ? "|init|" : "|");
			foreach (var instr in body.Instructions) {
				sb.Append(instr.OpCode.Code.ToString());
				sb.Append(' ');
				AppendOperand(sb, instr.Operand, index);
				sb.Append('\n');
			}
			foreach (var eh in body.ExceptionHandlers) {
				sb.Append(eh.HandlerType.ToString()).Append(' ');
				AppendOperand(sb, eh.TryStart, index);
				AppendOperand(sb, eh.TryEnd, index);
				AppendOperand(sb, eh.HandlerStart, index);
				AppendOperand(sb, eh.HandlerEnd, index);
				AppendOperand(sb, eh.FilterStart, index);
				sb.Append(eh.CatchType?.FullName).Append('\n');
			}
			return sb.ToString();
		}

		static void AppendOperand(StringBuilder sb, object? operand, Dictionary<Instruction, int> index) {
			switch (operand) {
			case null:
				break;
			case Instruction target:
				sb.Append('@').Append(index.TryGetValue(target, out var i) ? i : -1);
				break;
			case IList<Instruction> targets:
				foreach (var t in targets)
					AppendOperand(sb, t, index);
				break;
			case Local local:
				sb.Append('L').Append(local.Index);
				break;
			case Parameter p:
				sb.Append('A').Append(p.Index);
				break;
			case IFullName fullName:
				sb.Append(fullName.FullName);
				break;
			case MethodSig sig:
				sb.Append(sig.ToString());
				break;
			case IFormattable f:
				sb.Append(f.ToString(null, CultureInfo.InvariantCulture));
				break;
			default:
				sb.Append(operand);
				break;
			}
			sb.Append(' ');
		}
	}
}
