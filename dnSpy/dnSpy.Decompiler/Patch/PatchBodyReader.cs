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
using System.Text;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.Patch {
	/// <summary>
	/// Reads a method body written by <see cref="PatchBodyWriter"/> into a module: types and members of the module are
	/// looked up by name, other types and members are referenced (existing references are reused)
	/// </summary>
	sealed class PatchBodyReader {
		readonly ModuleDef module;
		readonly string? assemblyName;
		readonly Dictionary<string, ITypeDefOrRef> typeRefs = new Dictionary<string, ITypeDefOrRef>(StringComparer.Ordinal);
		readonly Dictionary<string, MemberRef> memberRefs = new Dictionary<string, MemberRef>(StringComparer.Ordinal);
		Dictionary<string, OpCode>? opCodes;
		bool existingRefsAdded;

		public PatchBodyReader(ModuleDef module) {
			this.module = module;
			assemblyName = module.Assembly?.Name.String;
		}

		public CilBody Read(MethodDef method, XElement elem) {
			var locals = elem.Elements("local").Select(a => new Local(ReadType(Single(a)))).ToList();
			var instrElems = elem.Elements("i").ToList();
			var instrs = instrElems.Select(a => new Instruction(GetOpCode((string?)a.Attribute("op")))).ToList();
			Instruction? At(string? s, bool endAllowed) {
				if (!int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i) || i < -1 || i >= instrs.Count || (i == -1 && !endAllowed))
					throw new PatchException("Invalid instruction index: " + s);
				return i == -1 ? null : instrs[i];
			}
			for (int i = 0; i < instrs.Count; i++)
				instrs[i].Operand = ReadOperand(method, instrs[i].OpCode, instrElems[i], locals, s => At(s, false)!);

			var body = new CilBody { InitLocals = (string?)elem.Attribute("initLocals") == "true" };
			foreach (var local in locals)
				body.Variables.Add(local);
			foreach (var instr in instrs)
				body.Instructions.Add(instr);
			foreach (var e in elem.Elements("eh")) {
				if (!Enum.TryParse<ExceptionHandlerType>((string?)e.Attribute("kind"), out var kind))
					throw new PatchException("Invalid exception handler");
				var eh = new ExceptionHandler(kind) {
					TryStart = At((string?)e.Attribute("tryStart"), false),
					TryEnd = At((string?)e.Attribute("tryEnd"), true),
					HandlerStart = At((string?)e.Attribute("handlerStart"), false),
					HandlerEnd = At((string?)e.Attribute("handlerEnd"), true),
				};
				if (e.Attribute("filterStart") is XAttribute filter)
					eh.FilterStart = At(filter.Value, false);
				if (e.Elements().FirstOrDefault() is XElement catchType)
					eh.CatchType = ReadTypeRef(catchType);
				body.ExceptionHandlers.Add(eh);
			}
			// The patched method may have needed other short/long branches than this module's version
			body.SimplifyBranches();
			body.OptimizeBranches();
			return body;
		}

		OpCode GetOpCode(string? name) {
			if (opCodes is null) {
				opCodes = new Dictionary<string, OpCode>(StringComparer.Ordinal);
				foreach (var op in OpCodes.OneByteOpCodes.Concat(OpCodes.TwoByteOpCodes)) {
					if (op.Code != Code.UNKNOWN1 && op.Code != Code.UNKNOWN2)
						opCodes[op.Name] = op;
				}
			}
			if (name is null || !opCodes.TryGetValue(name, out var opCode))
				throw new PatchException("Unknown opcode: " + name);
			return opCode;
		}

		static XElement Single(XElement elem) => elem.Elements().FirstOrDefault() ?? throw new PatchException($"Invalid patch file: empty <{elem.Name}>");

		object? ReadOperand(MethodDef method, OpCode opCode, XElement elem, List<Local> locals, Func<string?, Instruction> at) {
			string Value() => (string?)elem.Attribute("value") ?? throw new PatchException($"Missing operand of {opCode.Name}");
			var inv = CultureInfo.InvariantCulture;
			try {
				switch (opCode.OperandType) {
				case OperandType.InlineNone:
					return null;
				case OperandType.InlineBrTarget:
				case OperandType.ShortInlineBrTarget:
					return at((string?)elem.Attribute("target"));
				case OperandType.InlineSwitch:
					var targets = (string?)elem.Attribute("targets") ?? string.Empty;
					return targets.Length == 0 ? Array.Empty<Instruction>() : targets.Split(',').Select(at).ToArray();
				case OperandType.ShortInlineI:
					if (opCode.Code == Code.Ldc_I4_S)
						return sbyte.Parse(Value(), NumberStyles.AllowLeadingSign, inv);
					return byte.Parse(Value(), NumberStyles.None, inv);
				case OperandType.InlineI:
					return int.Parse(Value(), NumberStyles.AllowLeadingSign, inv);
				case OperandType.InlineI8:
					return long.Parse(Value(), NumberStyles.AllowLeadingSign, inv);
				case OperandType.ShortInlineR:
					return float.Parse(Value(), NumberStyles.Float, inv);
				case OperandType.InlineR:
					return double.Parse(Value(), NumberStyles.Float, inv);
				case OperandType.InlineString:
					if (elem.Attribute("string64") is XAttribute b64)
						return Encoding.Unicode.GetString(Convert.FromBase64String(b64.Value));
					return (string?)elem.Attribute("string") ?? throw new PatchException($"Missing operand of {opCode.Name}");
				case OperandType.ShortInlineVar:
				case OperandType.InlineVar:
					int index = int.Parse((string?)elem.Attribute("index") ?? "-1", NumberStyles.AllowLeadingSign, inv);
					bool isArg = opCode.Code is Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S or Code.Starg or Code.Starg_S;
					if (isArg) {
						if ((uint)index >= (uint)method.Parameters.Count)
							throw new PatchException("Invalid parameter index " + index);
						return method.Parameters[index];
					}
					if ((uint)index >= (uint)locals.Count)
						throw new PatchException("Invalid local index " + index);
					return locals[index];
				case OperandType.InlineType:
					return ReadTypeRef(Single(elem));
				case OperandType.InlineMethod:
					return ReadMethod(Single(elem));
				case OperandType.InlineField:
					return ReadField(Single(elem));
				case OperandType.InlineTok:
					var tok = Single(elem);
					return tok.Name.LocalName switch {
						"field" or "arraydata" => ReadField(tok),
						"method" or "mspec" or "varargcall" => ReadMethod(tok),
						_ => ReadTypeRef(tok),
					};
				case OperandType.InlineSig:
					return ReadMethodSig(Single(elem));
				default:
					throw new PatchException("Unsupported instruction " + opCode.Name);
				}
			}
			catch (FormatException) {
				throw new PatchException($"Invalid operand of {opCode.Name}");
			}
			catch (OverflowException) {
				throw new PatchException($"Invalid operand of {opCode.Name}");
			}
		}

		ITypeDefOrRef ReadTypeRef(XElement elem) {
			if (elem.Name == "spec") {
				var specKey = elem.ToString(SaveOptions.DisableFormatting);
				if (!typeRefs.TryGetValue(specKey, out var spec))
					typeRefs[specKey] = spec = new TypeSpecUser(ReadType(Single(elem)));
				return spec;
			}
			if (elem.Name != "ref")
				throw new PatchException($"Invalid patch file: <{elem.Name}> isn't a type");
			var name = (string?)elem.Attribute("name") ?? throw new PatchException("Invalid patch file: a type without a name");
			var ns = (string?)elem.Attribute("ns") ?? string.Empty;
			var asm = (string?)elem.Attribute("asm");
			var key = name + "," + asm;
			if (typeRefs.TryGetValue(key, out var type))
				return type;

			if (asm is null || (assemblyName is not null && new AssemblyNameInfo(asm).Name == assemblyName))
				type = module.Find(name, false) ?? throw new PatchException("Type not found: " + name);
			else
				type = FindTypeRef(name, asm) ?? CreateTypeRef(name, ns, asm);
			typeRefs[key] = type;
			return type;
		}

		TypeRef? FindTypeRef(string name, string asm) {
			var asmName = new AssemblyNameInfo(asm).Name;
			foreach (var tr in module.GetTypeRefs()) {
				if (tr.FullName == name && tr.DefinitionAssembly?.Name == asmName)
					return tr;
			}
			return null;
		}

		TypeRef CreateTypeRef(string name, string ns, string asm) {
			var asmName = new AssemblyNameInfo(asm);
			IResolutionScope scope = module.GetAssemblyRefs().FirstOrDefault(a => a.Name == asmName.Name) ?? (IResolutionScope)new AssemblyRefUser(asmName);
			var parts = name.Split('/');
			var top = parts[0];
			if (ns.Length != 0 && top.StartsWith(ns + ".", StringComparison.Ordinal))
				top = top.Substring(ns.Length + 1);
			else
				ns = string.Empty;
			var tr = new TypeRefUser(module, ns, top, scope);
			for (int i = 1; i < parts.Length; i++)
				tr = new TypeRefUser(module, string.Empty, parts[i], tr);
			return tr;
		}

		TypeSig ReadType(XElement elem) {
			uint N() => uint.Parse((string?)elem.Attribute("n") ?? string.Empty, NumberStyles.None, CultureInfo.InvariantCulture);
			XElement Child(int i) => elem.Elements().ElementAtOrDefault(i) ?? throw new PatchException($"Invalid patch file: incomplete <{elem.Name}>");
			try {
				switch (elem.Name.LocalName) {
				case "t":
					if (!Enum.TryParse<ElementType>((string?)elem.Attribute("e"), out var et))
						throw new PatchException("Invalid patch file: unknown corlib type");
					return GetCorLibType(et) ?? throw new PatchException("Invalid patch file: unknown corlib type");
				case "class":
					return ToSig(ReadTypeRef(Child(0)), isValueType: false);
				case "valuetype":
					return ToSig(ReadTypeRef(Child(0)), isValueType: true);
				case "generic":
					var genericType = ReadType(Child(0)) as ClassOrValueTypeSig ?? throw new PatchException("Invalid patch file: generic type");
					return new GenericInstSig(genericType, elem.Elements().Skip(1).Select(ReadType).ToList());
				case "var":
					return new GenericVar(N());
				case "mvar":
					return new GenericMVar(N());
				case "szarray":
					return new SZArraySig(ReadType(Child(0)));
				case "array":
					var inv = CultureInfo.InvariantCulture;
					return new ArraySig(ReadType(Child(0)), uint.Parse((string?)elem.Attribute("rank") ?? string.Empty, NumberStyles.None, inv),
						ParseList((string?)elem.Attribute("sizes"), a => uint.Parse(a, NumberStyles.None, inv)),
						ParseList((string?)elem.Attribute("lowerBounds"), a => int.Parse(a, NumberStyles.AllowLeadingSign, inv)));
				case "ptr":
					return new PtrSig(ReadType(Child(0)));
				case "byref":
					return new ByRefSig(ReadType(Child(0)));
				case "pinned":
					return new PinnedSig(ReadType(Child(0)));
				case "modreq":
					return new CModReqdSig(ReadTypeRef(Child(0)), ReadType(Child(1)));
				case "modopt":
					return new CModOptSig(ReadTypeRef(Child(0)), ReadType(Child(1)));
				case "fnptr":
					return new FnPtrSig(ReadMethodSig(Child(0)));
				case "sentinel":
					return new SentinelSig();
				default:
					throw new PatchException($"Invalid patch file: <{elem.Name}> isn't a type");
				}
			}
			catch (FormatException) {
				throw new PatchException($"Invalid patch file: <{elem.Name}>");
			}
			catch (OverflowException) {
				throw new PatchException($"Invalid patch file: <{elem.Name}>");
			}
		}

		static List<T> ParseList<T>(string? s, Func<string, T> parse) => string.IsNullOrEmpty(s) ? new List<T>() : s!.Split(',').Select(parse).ToList();

		CorLibTypeSig? GetCorLibType(ElementType et) {
			var c = module.CorLibTypes;
			return et switch {
				ElementType.Void => c.Void,
				ElementType.Boolean => c.Boolean,
				ElementType.Char => c.Char,
				ElementType.I1 => c.SByte,
				ElementType.U1 => c.Byte,
				ElementType.I2 => c.Int16,
				ElementType.U2 => c.UInt16,
				ElementType.I4 => c.Int32,
				ElementType.U4 => c.UInt32,
				ElementType.I8 => c.Int64,
				ElementType.U8 => c.UInt64,
				ElementType.R4 => c.Single,
				ElementType.R8 => c.Double,
				ElementType.String => c.String,
				ElementType.TypedByRef => c.TypedReference,
				ElementType.I => c.IntPtr,
				ElementType.U => c.UIntPtr,
				ElementType.Object => c.Object,
				_ => null,
			};
		}

		// System.Int32 etc. must be their corlib signatures, or the module's methods aren't found by signature
		TypeSig ToSig(ITypeDefOrRef type, bool isValueType) {
			var corlib = module.CorLibTypes.GetCorLibTypeSig(type);
			if (corlib is not null)
				return corlib;
			return isValueType ? new ValueTypeSig(type) : new ClassSig(type);
		}

		MethodSig ReadMethodSig(XElement elem) {
			if (elem.Name != "msig")
				throw new PatchException($"Invalid patch file: <{elem.Name}> isn't a method signature");
			var inv = CultureInfo.InvariantCulture;
			if (!byte.TryParse((string?)elem.Attribute("cc"), NumberStyles.None, inv, out byte cc) ||
				!uint.TryParse((string?)elem.Attribute("gp"), NumberStyles.None, inv, out uint gp))
				throw new PatchException("Invalid patch file: method signature");
			var types = elem.Elements().Where(a => a.Name != "vararg").Select(ReadType).ToList();
			if (types.Count == 0)
				throw new PatchException("Invalid patch file: method signature without a return type");
			var sig = new MethodSig((CallingConvention)cc, gp, types[0], types.Skip(1).ToList());
			if (elem.Element("vararg") is XElement vararg)
				sig.ParamsAfterSentinel = vararg.Elements().Select(ReadType).ToList();
			return sig;
		}

		IMethod ReadMethod(XElement elem) {
			switch (elem.Name.LocalName) {
			case "mspec":
				var method = ReadMethod(Single(elem)) as IMethodDefOrRef ?? throw new PatchException("Invalid patch file: generic method");
				return new MethodSpecUser(method, new GenericInstMethodSig(elem.Elements().Skip(1).Select(ReadType).ToList()));
			case "varargcall":
				var varargMethod = ReadMethod(Single(elem)) as MethodDef ?? throw new PatchException("Invalid patch file: vararg call");
				var callSig = ReadMethodSig(elem.Elements().ElementAtOrDefault(1) ?? throw new PatchException("Invalid patch file: vararg call"));
				return GetMemberRef(() => new MemberRefUser(module, varargMethod.Name, callSig, varargMethod));
			case "method":
				var name = (string?)elem.Attribute("name") ?? throw new PatchException("Invalid patch file: a method without a name");
				var declType = ReadTypeRef(Single(elem));
				var sig = ReadMethodSig(elem.Elements().ElementAtOrDefault(1) ?? throw new PatchException("Invalid patch file: a method without a signature"));
				if (declType is TypeDef td)
					return td.FindMethod(name, sig) ?? throw new PatchException($"Method not found: {td.FullName}::{name}");
				return GetMemberRef(() => new MemberRefUser(module, name, sig, declType));
			default:
				throw new PatchException($"Invalid patch file: <{elem.Name}> isn't a method");
			}
		}

		IField ReadField(XElement elem) {
			if (elem.Name == "arraydata")
				return ReadArrayData(elem);
			if (elem.Name != "field")
				throw new PatchException($"Invalid patch file: <{elem.Name}> isn't a field");
			var name = (string?)elem.Attribute("name") ?? throw new PatchException("Invalid patch file: a field without a name");
			var declType = ReadTypeRef(Single(elem));
			var sig = new FieldSig(ReadType(elem.Elements().ElementAtOrDefault(1) ?? throw new PatchException("Invalid patch file: a field without a type")));
			if (declType is TypeDef td)
				return td.FindField(name, sig) ?? throw new PatchException($"Field not found: {td.FullName}::{name}");
			return GetMemberRef(() => new MemberRefUser(module, name, sig, declType));
		}

		// The field with an array initializer's data in <PrivateImplementationDetails>: this module's if it has one with
		// the same name and data (the name is a hash of the data), else a new one
		FieldDef ReadArrayData(XElement elem) {
			var name = (string?)elem.Attribute("name") ?? throw new PatchException("Invalid patch file: array data without a name");
			byte[] data;
			try {
				data = Convert.FromBase64String((string?)elem.Attribute("data") ?? string.Empty);
			}
			catch (FormatException) {
				throw new PatchException("Invalid patch file: array data");
			}
			var details = module.Types.FirstOrDefault(a => a.Namespace.String.Length == 0 && a.Name == "<PrivateImplementationDetails>");
			if (details is null) {
				details = new TypeDefUser(string.Empty, "<PrivateImplementationDetails>", module.CorLibTypes.Object.TypeDefOrRef) {
					Attributes = TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass,
				};
				module.Types.Add(details);
			}
			var existing = details.Fields.FirstOrDefault(a => a.Name == name);
			if (existing is not null) {
				if (existing.InitialValue is null || !existing.InitialValue.SequenceEqual(data))
					throw new PatchException("Other array data with the same name: " + name);
				return existing;
			}

			var typeElem = Single(elem);
			TypeSig fieldType;
			if (typeElem.Name == "blob") {
				if (!uint.TryParse((string?)typeElem.Attribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out uint size) || size < data.Length)
					throw new PatchException("Invalid patch file: array data size");
				var typeName = "__StaticArrayInitTypeSize=" + size.ToString(CultureInfo.InvariantCulture);
				var sizeType = details.NestedTypes.FirstOrDefault(a => a.Name == typeName && a.ClassSize == size);
				if (sizeType is null) {
					sizeType = new TypeDefUser(string.Empty, typeName, module.CorLibTypes.GetTypeRef("System", "ValueType")) {
						Attributes = TypeAttributes.NestedPrivate | TypeAttributes.ExplicitLayout | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
						ClassLayout = new ClassLayoutUser(1, size),
					};
					details.NestedTypes.Add(sizeType);
				}
				fieldType = new ValueTypeSig(sizeType);
			}
			else
				fieldType = ReadType(typeElem);
			var field = new FieldDefUser(name, new FieldSig(fieldType), FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.HasFieldRVA) {
				InitialValue = data,
			};
			details.Fields.Add(field);
			return field;
		}

		// Reuses the module's own references so the patched module doesn't get duplicate rows
		MemberRef GetMemberRef(Func<MemberRef> create) {
			if (!existingRefsAdded) {
				existingRefsAdded = true;
				foreach (var mr in module.GetMemberRefs())
					memberRefs[Key(mr)] = mr;
			}
			var newRef = create();
			var key = Key(newRef);
			if (memberRefs.TryGetValue(key, out var existing))
				return existing;
			memberRefs[key] = newRef;
			return newRef;
		}

		static string Key(MemberRef mr) => mr.FullName + "|" + mr.DeclaringType?.DefinitionAssembly?.Name;
	}
}
