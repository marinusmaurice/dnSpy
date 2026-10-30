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
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.Patch {
	/// <summary>
	/// Writes a method body as XML with names instead of tokens, see <see cref="PatchBodyReader"/>:
	/// <list type="bullet">
	/// <item><c>&lt;body initLocals&gt;</c> with <c>&lt;local&gt;</c>, <c>&lt;i op&gt;</c> (instructions) and <c>&lt;eh&gt;</c> (exception handlers)</item>
	/// <item>types: <c>&lt;t e="I4"/&gt;</c> (corlib), <c>&lt;class&gt;</c>/<c>&lt;valuetype&gt;</c> with a type reference, <c>&lt;generic&gt;</c>, <c>&lt;szarray&gt;</c>, <c>&lt;var n&gt;</c>, ...</item>
	/// <item>type references: <c>&lt;ref ns name asm/&gt;</c> (no <c>asm</c>: the patched assembly), <c>&lt;spec&gt;</c> (a type signature)</item>
	/// <item>members: <c>&lt;method name&gt;</c>, <c>&lt;field name&gt;</c> (declaring type, then signature), <c>&lt;mspec&gt;</c> (generic method instance)</item>
	/// </list>
	/// </summary>
	sealed class PatchBodyWriter {
		readonly ModuleDef module;
		readonly string? assemblyName;

		public PatchBodyWriter(ModuleDef module) {
			this.module = module;
			assemblyName = module.Assembly?.Name.String;
		}

		public XElement Write(MethodDef method) {
			var body = method.Body ?? throw new PatchException("The method has no body: " + method.FullName);
			var index = new Dictionary<Instruction, int>();
			for (int i = 0; i < body.Instructions.Count; i++)
				index[body.Instructions[i]] = i;

			var elem = new XElement("body", new XAttribute("initLocals", body.InitLocals ? "true" : "false"));
			foreach (var local in body.Variables)
				elem.Add(new XElement("local", WriteType(local.Type)));
			foreach (var instr in body.Instructions) {
				var i = new XElement("i", new XAttribute("op", instr.OpCode.Name));
				WriteOperand(i, instr, index);
				elem.Add(i);
			}
			foreach (var eh in body.ExceptionHandlers) {
				var e = new XElement("eh", new XAttribute("kind", eh.HandlerType.ToString()),
					new XAttribute("tryStart", Index(eh.TryStart, index)),
					new XAttribute("tryEnd", Index(eh.TryEnd, index)),
					new XAttribute("handlerStart", Index(eh.HandlerStart, index)),
					new XAttribute("handlerEnd", Index(eh.HandlerEnd, index)));
				if (eh.FilterStart is not null)
					e.Add(new XAttribute("filterStart", Index(eh.FilterStart, index)));
				if (eh.CatchType is not null)
					e.Add(WriteTypeRef(eh.CatchType));
				elem.Add(e);
			}
			return elem;
		}

		// -1 is the end of the body
		static string Index(Instruction? instr, Dictionary<Instruction, int> index) {
			if (instr is null)
				return "-1";
			if (!index.TryGetValue(instr, out int i))
				throw new PatchException("A branch or exception handler points outside the method body");
			return i.ToString(CultureInfo.InvariantCulture);
		}

		void WriteOperand(XElement elem, Instruction instr, Dictionary<Instruction, int> index) {
			var operand = instr.Operand;
			switch (instr.OpCode.OperandType) {
			case OperandType.InlineNone:
				break;
			case OperandType.InlineBrTarget:
			case OperandType.ShortInlineBrTarget:
				elem.Add(new XAttribute("target", Index(operand as Instruction, index)));
				break;
			case OperandType.InlineSwitch:
				elem.Add(new XAttribute("targets", string.Join(",", ((IList<Instruction>)operand).Select(a => Index(a, index)))));
				break;
			case OperandType.ShortInlineI:
			case OperandType.InlineI:
			case OperandType.InlineI8:
			case OperandType.ShortInlineR:
			case OperandType.InlineR:
				elem.Add(new XAttribute("value", operand switch {
					float f => f.ToString("R", CultureInfo.InvariantCulture),
					double d => d.ToString("R", CultureInfo.InvariantCulture),
					IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
					_ => throw new PatchException($"Invalid operand of {instr.OpCode.Name}"),
				}));
				break;
			case OperandType.InlineString:
				WriteString(elem, operand as string ?? string.Empty);
				break;
			case OperandType.ShortInlineVar:
			case OperandType.InlineVar:
				elem.Add(new XAttribute("index", operand switch {
					Local local => local.Index,
					Parameter p => p.Index,
					_ => throw new PatchException($"Invalid operand of {instr.OpCode.Name}"),
				}));
				break;
			case OperandType.InlineType:
				elem.Add(WriteTypeRef((ITypeDefOrRef)operand));
				break;
			case OperandType.InlineMethod:
				elem.Add(WriteMethod((IMethod)operand));
				break;
			case OperandType.InlineField:
				elem.Add(WriteField((IField)operand));
				break;
			case OperandType.InlineTok:
				elem.Add(operand switch {
					ITypeDefOrRef type => WriteTypeRef(type),
					IField field when field.IsField => WriteField(field),
					IMethod method => WriteMethod(method),
					_ => throw new PatchException($"Invalid operand of {instr.OpCode.Name}"),
				});
				break;
			case OperandType.InlineSig:
				elem.Add(WriteMethodSig((MethodSig)operand));
				break;
			default:
				throw new PatchException($"Unsupported instruction {instr.OpCode.Name}");
			}
		}

		// XML can't hold all characters (eg. \0): such strings are saved as UTF-16 in base64
		static void WriteString(XElement elem, string s) {
			if (IsValidXml(s))
				elem.Add(new XAttribute("string", s));
			else
				elem.Add(new XAttribute("string64", Convert.ToBase64String(Encoding.Unicode.GetBytes(s))));
		}

		static bool IsValidXml(string s) {
			try {
				XmlConvert.VerifyXmlChars(s);
			}
			catch (XmlException) {
				return false;
			}
			// \r and \t are valid but an attribute value doesn't keep them as they are
			return s.IndexOf('\r') < 0 && s.IndexOf('\t') < 0 && s.IndexOf('\n') < 0;
		}

		XElement WriteTypeRef(ITypeDefOrRef type) {
			if (type is TypeSpec spec)
				return new XElement("spec", WriteType(spec.TypeSig));
			var elem = new XElement("ref", new XAttribute("name", type.FullName));
			var top = type;
			while (top.DeclaringType is ITypeDefOrRef declType)
				top = declType;
			elem.Add(new XAttribute("ns", top.Namespace));
			if (!IsThisAssembly(type)) {
				var asm = type.DefinitionAssembly ?? throw new PatchException("Unknown assembly of type " + type.FullName);
				elem.Add(new XAttribute("asm", asm.FullName));
			}
			return elem;
		}

		bool IsThisAssembly(ITypeDefOrRef type) {
			if (type is TypeDef td)
				return td.Module == module;
			var scope = ((TypeRef)type).GetNonNestedTypeRefScope();
			if (scope is ModuleDef || scope is ModuleRef)
				return true;
			return scope is AssemblyRef asmRef && assemblyName is not null && asmRef.Name == assemblyName;
		}

		XElement WriteType(TypeSig? sig) {
			switch (sig) {
			case null:
				throw new PatchException("Missing type");
			case CorLibTypeSig corlib:
				return new XElement("t", new XAttribute("e", corlib.ElementType.ToString()));
			case ClassSig cls:
				return new XElement("class", WriteTypeRef(cls.TypeDefOrRef));
			case ValueTypeSig vt:
				return new XElement("valuetype", WriteTypeRef(vt.TypeDefOrRef));
			case GenericInstSig gis:
				return new XElement("generic", WriteType(gis.GenericType), gis.GenericArguments.Select(WriteType));
			case GenericVar gv:
				return new XElement("var", new XAttribute("n", gv.Number));
			case GenericMVar gmv:
				return new XElement("mvar", new XAttribute("n", gmv.Number));
			case SZArraySig sz:
				return new XElement("szarray", WriteType(sz.Next));
			case ArraySig arr:
				return new XElement("array", new XAttribute("rank", arr.Rank),
					new XAttribute("sizes", string.Join(",", arr.Sizes)),
					new XAttribute("lowerBounds", string.Join(",", arr.LowerBounds)),
					WriteType(arr.Next));
			case PtrSig ptr:
				return new XElement("ptr", WriteType(ptr.Next));
			case ByRefSig byRef:
				return new XElement("byref", WriteType(byRef.Next));
			case PinnedSig pinned:
				return new XElement("pinned", WriteType(pinned.Next));
			case CModReqdSig reqd:
				return new XElement("modreq", WriteTypeRef(reqd.Modifier), WriteType(reqd.Next));
			case CModOptSig opt:
				return new XElement("modopt", WriteTypeRef(opt.Modifier), WriteType(opt.Next));
			case FnPtrSig fnPtr:
				return new XElement("fnptr", WriteMethodSig(fnPtr.MethodSig));
			case SentinelSig:
				return new XElement("sentinel");
			default:
				throw new PatchException("Unsupported type " + sig.FullName);
			}
		}

		XElement WriteMethodSig(MethodSig sig) {
			var elem = new XElement("msig", new XAttribute("cc", ((byte)sig.CallingConvention).ToString(CultureInfo.InvariantCulture)),
				new XAttribute("gp", sig.GenParamCount.ToString(CultureInfo.InvariantCulture)),
				WriteType(sig.RetType), sig.Params.Select(WriteType));
			if (sig.ParamsAfterSentinel is not null)
				elem.Add(new XElement("vararg", sig.ParamsAfterSentinel.Select(WriteType)));
			return elem;
		}

		XElement WriteMethod(IMethod method) {
			switch (method) {
			case MethodSpec spec:
				return new XElement("mspec", WriteMethod(spec.Method), spec.GenericInstMethodSig.GenericArguments.Select(WriteType));
			case MethodDef md:
				return new XElement("method", new XAttribute("name", md.Name.String), WriteTypeRef(md.DeclaringType), WriteMethodSig(md.MethodSig));
			case MemberRef mr when mr.Class is ITypeDefOrRef declType:
				return new XElement("method", new XAttribute("name", mr.Name.String), WriteTypeRef(declType), WriteMethodSig(mr.MethodSig));
			case MemberRef mr when mr.Class is MethodDef varargMethod:
				// A call site of a vararg method: the method, then the call site's signature
				return new XElement("varargcall", WriteMethod(varargMethod), WriteMethodSig(mr.MethodSig));
			default:
				throw new PatchException("Unsupported method reference " + method.FullName);
			}
		}

		static bool IsArrayData(FieldDef field) =>
			field.HasFieldRVA && field.InitialValue is not null && field.IsStatic && field.DeclaringType is TypeDef type &&
			type.DeclaringType is null && type.Name.String.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal);

		XElement WriteField(IField field) {
			switch (field) {
			case FieldDef fd when IsArrayData(fd):
				// Array initializer data: the data goes in the patch, the other version may not have it
				var dataType = fd.FieldType is CorLibTypeSig ? WriteType(fd.FieldType) :
					new XElement("blob", new XAttribute("size", fd.FieldType.ToTypeDefOrRef()?.ResolveTypeDef()?.ClassSize ?? (uint)fd.InitialValue.Length));
				return new XElement("arraydata", new XAttribute("name", fd.Name.String), new XAttribute("data", Convert.ToBase64String(fd.InitialValue)), dataType);
			case FieldDef fd:
				return new XElement("field", new XAttribute("name", fd.Name.String), WriteTypeRef(fd.DeclaringType), WriteType(fd.FieldType));
			case MemberRef mr when mr.Class is ITypeDefOrRef declType:
				return new XElement("field", new XAttribute("name", mr.Name.String), WriteTypeRef(declType), WriteType(mr.FieldSig?.Type));
			default:
				throw new PatchException("Unsupported field reference " + field.FullName);
			}
		}
	}
}
