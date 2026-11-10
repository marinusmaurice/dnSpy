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
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.Rename {
	/// <summary>
	/// Fingerprints of types and members that don't depend on the names of the module's own types and members, so a
	/// type or member can be found again after it was renamed, or in another build where an obfuscator gave it
	/// another name. Types of other assemblies are kept by name; the module's own types and members become <c>?</c>.
	/// </summary>
	sealed class RenameFingerprint {
		readonly ModuleDef module;
		readonly UTF8String? assemblyName;
		readonly Dictionary<IMemberDef, string> cache = new Dictionary<IMemberDef, string>();

		public RenameFingerprint(ModuleDef module) {
			this.module = module;
			assemblyName = module.Assembly?.Name;
		}

		public static string Hash(string text) {
			using (var sha = SHA256.Create()) {
				var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
				var sb = new StringBuilder(12);
				for (int i = 0; i < 6; i++)
					sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
				return sb.ToString();
			}
		}

		public string Type(TypeDef type) {
			if (cache.TryGetValue(type, out var fp))
				return fp;
			var sb = new StringBuilder();
			sb.Append("T|").Append((uint)type.Attributes).Append('|').Append(type.GenericParameters.Count).Append('|').Append(Shape(type.BaseType)).Append('|');
			foreach (var i in type.Interfaces.Select(a => Shape(a.Interface)).OrderBy(a => a, StringComparer.Ordinal))
				sb.Append(i).Append(';');
			sb.Append('|').Append(type.NestedTypes.Count).Append('\n');
			foreach (var m in MemberFingerprints(type).OrderBy(a => a, StringComparer.Ordinal))
				sb.Append(m).Append('\n');
			fp = Hash(sb.ToString());
			cache[type] = fp;
			return fp;
		}

		/// <summary>The fingerprints of the type's methods, fields, properties and events (not of its nested types)</summary>
		public IEnumerable<string> MemberFingerprints(TypeDef type) {
			foreach (var m in type.Methods)
				yield return Member(m);
			foreach (var f in type.Fields)
				yield return Member(f);
			foreach (var p in type.Properties)
				yield return Member(p);
			foreach (var e in type.Events)
				yield return Member(e);
		}

		public string Member(IMemberDef member) {
			if (cache.TryGetValue(member, out var fp))
				return fp;
			var sb = new StringBuilder();
			switch (member) {
			case MethodDef method:
				sb.Append("M|").Append((uint)method.Attributes).Append('|').Append((uint)method.ImplAttributes).Append('|').Append(Signature(method)).Append('\n');
				AppendBody(sb, method);
				break;
			case FieldDef field:
				sb.Append("F|").Append((uint)field.Attributes).Append('|').Append(Signature(field)).Append('|');
				if (field.Constant is not null)
					sb.Append(Convert.ToString(field.Constant.Value, CultureInfo.InvariantCulture));
				sb.Append('|');
				if (field.InitialValue is byte[] data)
					sb.Append(Convert.ToBase64String(data));
				break;
			case PropertyDef prop:
				sb.Append("P|").Append((uint)prop.Attributes).Append('|').Append(Signature(prop));
				foreach (var m in prop.GetMethods.Concat(prop.SetMethods))
					sb.Append('|').Append(Member(m));
				break;
			case EventDef evt:
				sb.Append("E|").Append((uint)evt.Attributes).Append('|').Append(Signature(evt));
				foreach (var m in new[] { evt.AddMethod, evt.RemoveMethod, evt.InvokeMethod })
					sb.Append('|').Append(m is null ? "-" : Member(m));
				break;
			}
			fp = Hash(sb.ToString());
			cache[member] = fp;
			return fp;
		}

		/// <summary>The member's signature without its name and with <c>?</c> for the module's own types, eg. <c>System.Void (?,System.String)</c></summary>
		public string Signature(IMemberDef member) => member switch {
			MethodDef method => Shape(method.MethodSig),
			FieldDef field => Shape(field.FieldSig?.Type),
			PropertyDef prop => Shape(prop.PropertySig),
			EventDef evt => Shape(evt.EventType),
			_ => string.Empty,
		};

		void AppendBody(StringBuilder sb, MethodDef method) {
			if (!method.HasBody)
				return;
			var body = method.Body;
			sb.Append(body.InitLocals ? "init" : "-").Append('|');
			foreach (var local in body.Variables)
				sb.Append(Shape(local.Type)).Append(';');
			sb.Append('\n');
			var instrs = body.Instructions;
			var index = new Dictionary<Instruction, int>(instrs.Count);
			for (int i = 0; i < instrs.Count; i++)
				index[instrs[i]] = i;
			foreach (var instr in instrs) {
				sb.Append(instr.OpCode.Code.ToString());
				var operand = Operand(instr.Operand, index);
				if (operand.Length != 0)
					sb.Append(' ').Append(operand);
				sb.Append('\n');
			}
			foreach (var eh in body.ExceptionHandlers) {
				sb.Append("eh ").Append(eh.HandlerType).Append(' ').Append(Index(eh.TryStart, index)).Append(' ').Append(Index(eh.TryEnd, index)).Append(' ')
					.Append(Index(eh.HandlerStart, index)).Append(' ').Append(Index(eh.HandlerEnd, index)).Append(' ').Append(Index(eh.FilterStart, index)).Append(' ')
					.Append(Shape(eh.CatchType)).Append('\n');
			}
		}

		static string Index(Instruction? instr, Dictionary<Instruction, int> index) =>
			instr is not null && index.TryGetValue(instr, out int i) ? i.ToString(CultureInfo.InvariantCulture) : "-";

		string Operand(object? operand, Dictionary<Instruction, int> index) => operand switch {
			null => string.Empty,
			Instruction target => "@" + Index(target, index),
			IList<Instruction> targets => string.Join(",", targets.Select(a => Index(a, index))),
			string s => "\"" + s + "\"",
			Local local => "L" + local.Index.ToString(CultureInfo.InvariantCulture),
			Parameter p => "A" + p.Index.ToString(CultureInfo.InvariantCulture),
			MethodSpec ms => MethodRef(ms.Method) + "<" + string.Join(",", ms.GenericInstMethodSig?.GenericArguments.Select(a => Shape(a)) ?? Array.Empty<string>()) + ">",
			MemberRef mr when mr.IsFieldRef => FieldRef(mr),
			MemberRef mr => MethodRef(mr),
			MethodDef md => MethodRef(md),
			FieldDef fd => FieldRef(fd),
			ITypeDefOrRef tdr => Shape(tdr),
			MethodSig sig => Shape(sig),
			IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
			_ => operand.GetType().Name,
		};

		string MethodRef(IMethodDefOrRef? method) {
			if (method is null)
				return "null";
			var declaringType = method.DeclaringType;
			if (method is MethodDef md ? md.Module == module : IsOwn(declaringType))
				return "?m:" + Shape(declaringType) + ":" + Shape(method.MethodSig);
			return method.FullName;
		}

		string FieldRef(IField field) {
			var declaringType = field.DeclaringType;
			if (field is FieldDef fd ? fd.Module == module : IsOwn(declaringType))
				return "?f:" + Shape(declaringType) + ":" + Shape(field.FieldSig?.Type);
			return field.FullName;
		}

		bool IsOwn(ITypeDefOrRef? type) {
			switch (type) {
			case TypeDef td:
				return td.Module == module;
			case TypeRef tr:
				return IsOwnScope(tr);
			case TypeSpec ts:
				var sig = ts.TypeSig.RemovePinnedAndModifiers();
				if (sig is GenericInstSig gis)
					sig = gis.GenericType;
				return sig is TypeDefOrRefSig tdrs && IsOwn(tdrs.TypeDefOrRef);
			default:
				return false;
			}
		}

		bool IsOwnScope(TypeRef tr) {
			for (int depth = 0; depth < 100; depth++) {
				switch (tr.ResolutionScope) {
				case TypeRef outer:
					tr = outer;
					continue;
				case ModuleDef:
					return true;
				case ModuleRef modRef:
					return StringComparer.OrdinalIgnoreCase.Equals(modRef.Name.String, module.Name.String);
				case AssemblyRef asmRef:
					return assemblyName is not null && UTF8String.CaseInsensitiveEquals(asmRef.Name, assemblyName);
				default:
					return false;
				}
			}
			return false;
		}

		public string Shape(ITypeDefOrRef? type) {
			if (type is null)
				return "-";
			if (type is TypeSpec ts)
				return Shape(ts.TypeSig);
			if (IsOwn(type))
				return "?";
			return type.FullName;
		}

		public string Shape(TypeSig? sig) {
			var sb = new StringBuilder();
			AppendShape(sb, sig, 0);
			return sb.ToString();
		}

		void AppendShape(StringBuilder sb, TypeSig? sig, int depth) {
			if (sig is null || depth > 50) {
				sb.Append('-');
				return;
			}
			switch (sig) {
			case TypeDefOrRefSig tdrs:
				sb.Append(Shape(tdrs.TypeDefOrRef));
				break;
			case GenericInstSig gis:
				AppendShape(sb, gis.GenericType, depth + 1);
				sb.Append('<');
				for (int i = 0; i < gis.GenericArguments.Count; i++) {
					if (i > 0)
						sb.Append(',');
					AppendShape(sb, gis.GenericArguments[i], depth + 1);
				}
				sb.Append('>');
				break;
			case GenericVar gv:
				sb.Append('!').Append(gv.Number);
				break;
			case GenericMVar gmv:
				sb.Append("!!").Append(gmv.Number);
				break;
			case PtrSig ptr:
				AppendShape(sb, ptr.Next, depth + 1);
				sb.Append('*');
				break;
			case ByRefSig byRef:
				AppendShape(sb, byRef.Next, depth + 1);
				sb.Append('&');
				break;
			case SZArraySig sz:
				AppendShape(sb, sz.Next, depth + 1);
				sb.Append("[]");
				break;
			case ArraySig array:
				AppendShape(sb, array.Next, depth + 1);
				sb.Append('[').Append(array.Rank).Append(']');
				break;
			case FnPtrSig fnPtr:
				sb.Append("fnptr(").Append(Shape(fnPtr.MethodSig)).Append(')');
				break;
			case ModifierSig mod:
				AppendShape(sb, mod.Next, depth + 1);
				break;
			case PinnedSig pinned:
				AppendShape(sb, pinned.Next, depth + 1);
				sb.Append(" pinned");
				break;
			default:
				sb.Append(sig.ElementType);
				break;
			}
		}

		public string Shape(CallingConventionSig? sig) {
			if (sig is not MethodBaseSig mbs)
				return sig is FieldSig fs ? Shape(fs.Type) : "-";
			var sb = new StringBuilder();
			if (mbs.HasThis)
				sb.Append("instance ");
			if (mbs.Generic)
				sb.Append('<').Append(mbs.GenParamCount).Append("> ");
			AppendShape(sb, mbs.RetType, 0);
			sb.Append(" (");
			for (int i = 0; i < mbs.Params.Count; i++) {
				if (i > 0)
					sb.Append(',');
				AppendShape(sb, mbs.Params[i], 0);
			}
			sb.Append(')');
			return sb.ToString();
		}
	}
}
