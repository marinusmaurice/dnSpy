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
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.StringDecryption {
	sealed class EmulatorException : Exception {
		public EmulatorException(string message) : base(message) { }
	}

	// Values on the stack, in locals, arguments and fields: int (int32, bool, char, int8, int16 and their unsigned versions),
	// long (int64, native int), double (float32, float64), null, string, real arrays and other objects of allowed .NET types,
	// and the objects below for the module's own types.
	sealed class FakeObject {
		public readonly TypeDef Type;
		public readonly Dictionary<FieldDef, object?> Fields = new Dictionary<FieldDef, object?>();
		public FakeObject(TypeDef type) => Type = type;
		public override string ToString() => Type.FullName;
	}
	sealed class FakeAssembly {
		public readonly ModuleDef Module;
		public FakeAssembly(ModuleDef module) => Module = module;
	}
	sealed class FakeType {
		public readonly TypeDef Type;
		public FakeType(TypeDef type) => Type = type;
		public override bool Equals(object? obj) => obj is FakeType other && other.Type == Type;
		public override int GetHashCode() => Type.GetHashCode();
	}
	sealed class FakeTypeHandle {
		public readonly TypeDef Type;
		public FakeTypeHandle(TypeDef type) => Type = type;
	}
	sealed class FakeFieldHandle {
		public readonly FieldDef Field;
		public FakeFieldHandle(FieldDef field) => Field = field;
	}

	abstract class Ref {
		public abstract object? Get();
		public abstract void Set(object? value);
	}

	/// <summary>
	/// Runs IL methods of a module without running any of the module's code: every instruction is emulated, and only
	/// methods of the allowed .NET types (<see cref="FrameworkCalls"/>) are called for real.
	/// </summary>
	sealed class ILEmulator {
		const int MaxDepth = 200;
		const int MaxArrayLength = 64 * 1024 * 1024;
		const long StaticConstructorSteps = 200_000_000;

		readonly ModuleDef module;
		readonly FrameworkCalls framework;
		readonly Dictionary<FieldDef, object?> staticFields = new Dictionary<FieldDef, object?>();
		readonly Dictionary<TypeDef, string?> cctorState = new Dictionary<TypeDef, string?>();
		readonly Dictionary<CilBody, Dictionary<Instruction, int>> indexes = new Dictionary<CilBody, Dictionary<Instruction, int>>();
		readonly FakeAssembly fakeAssembly;
		readonly ICorLibTypes corLib;
		long steps;
		long stepLimit;
		int depth;
		// The calls in a static constructor of <Module> that failed and were skipped
		public List<string> SkippedInitializers { get; } = new List<string>();

		public ILEmulator(ModuleDef module, FrameworkCalls framework) {
			this.module = module;
			this.framework = framework;
			fakeAssembly = new FakeAssembly(module);
			corLib = module.CorLibTypes;
		}

		readonly struct GenericArgs {
			public readonly IList<TypeSig>? Type;
			public readonly IList<TypeSig>? Method;
			public GenericArgs(IList<TypeSig>? type, IList<TypeSig>? method) {
				Type = type;
				Method = method;
			}

			public TypeSig Substitute(TypeSig sig) {
				if (Type is null && Method is null)
					return sig;
				switch (sig) {
				case GenericMVar mvar when Method is not null && mvar.Number < Method.Count:
					return Method[(int)mvar.Number];
				case GenericVar var when Type is not null && var.Number < Type.Count:
					return Type[(int)var.Number];
				case SZArraySig sz:
					return new SZArraySig(Substitute(sz.Next));
				case ByRefSig br:
					return new ByRefSig(Substitute(br.Next));
				case CModReqdSig or CModOptSig or PinnedSig:
					return Substitute(sig.Next);
				case GenericInstSig gis:
					var self = this;
					return new GenericInstSig(gis.GenericType, gis.GenericArguments.Select(a => self.Substitute(a)).ToList());
				default:
					return sig;
				}
			}
		}

		sealed class Frame {
			public readonly MethodDef Method;
			public readonly CilBody Body;
			public readonly object?[] Args;
			public readonly object?[] Locals;
			public readonly GenericArgs Generic;
			public readonly Dictionary<Instruction, int> Index;
			public Frame(MethodDef method, CilBody body, object?[] args, object?[] locals, GenericArgs generic, Dictionary<Instruction, int> index) {
				Method = method;
				Body = body;
				Args = args;
				Locals = locals;
				Generic = generic;
				Index = index;
			}
		}

		sealed class SlotRef : Ref {
			readonly ILEmulator emulator;
			readonly object?[] slots;
			readonly int index;
			readonly TypeSig type;
			public SlotRef(ILEmulator emulator, object?[] slots, int index, TypeSig type) {
				this.emulator = emulator;
				this.slots = slots;
				this.index = index;
				this.type = type;
			}
			public override object? Get() => slots[index];
			public override void Set(object? value) => slots[index] = emulator.ToSlot(type, value);
		}

		sealed class ArrayRef : Ref {
			readonly Array array;
			readonly int index;
			public ArrayRef(Array array, int index) {
				this.array = array;
				this.index = index;
			}
			public override object? Get() => LoadElement(Code.Ldelem, array, index);
			public override void Set(object? value) => StoreElement(array, index, value);
		}

		sealed class FieldRef : Ref {
			readonly ILEmulator emulator;
			readonly Dictionary<FieldDef, object?> fields;
			readonly FieldDef field;
			public FieldRef(ILEmulator emulator, Dictionary<FieldDef, object?> fields, FieldDef field) {
				this.emulator = emulator;
				this.fields = fields;
				this.field = field;
			}
			public override object? Get() => emulator.LoadField(fields, field);
			public override void Set(object? value) => fields[field] = emulator.ToSlot(field.FieldType, value);
		}

		/// <summary>
		/// Calls <paramref name="method"/> with <paramref name="args"/> (already converted for the parameters),
		/// running at most <paramref name="maxSteps"/> instructions
		/// </summary>
		public object? Call(MethodDef method, IList<TypeSig>? methodGenericArguments, object?[] args, long maxSteps) {
			steps = 0;
			stepLimit = maxSteps;
			depth = 0;
			EnsureStaticConstructor(method.DeclaringType);
			return Execute(method, new GenericArgs(null, methodGenericArguments), args);
		}

		/// <summary>
		/// Converts a constant to the value a parameter of type <paramref name="type"/> holds
		/// </summary>
		public object? ToParameter(TypeSig type, object? value) => ToSlot(type, value);

		object? Execute(MethodDef method, GenericArgs generic, object?[] args) {
			if (++depth > MaxDepth)
				throw new EmulatorException("Too many nested calls");
			try {
				var body = method.Body;
				if (body is null)
					throw new EmulatorException($"{method} has no IL code");
				var locals = new object?[body.Variables.Count];
				for (int i = 0; i < locals.Length; i++)
					locals[i] = Default(generic.Substitute(body.Variables[i].Type));
				var frame = new Frame(method, body, args, locals, generic, GetIndex(body));
				return Run(frame, 0, false);
			}
			finally {
				depth--;
			}
		}

		Dictionary<Instruction, int> GetIndex(CilBody body) {
			if (!indexes.TryGetValue(body, out var index)) {
				index = new Dictionary<Instruction, int>(body.Instructions.Count);
				for (int i = 0; i < body.Instructions.Count; i++)
					index[body.Instructions[i]] = i;
				indexes[body] = index;
			}
			return index;
		}

		bool IsOwn(TypeDef? type) => type is not null && type.Module == module;

		// A type of the module (a generic instance of one too), without resolving references to other assemblies:
		// resolving them would load those assemblies (in dnSpy, into the assembly explorer)
		TypeDef? GetOwnTypeDef(ITypeDefOrRef? type) => type switch {
			TypeDef td when IsOwn(td) => td,
			TypeSpec ts when ts.TypeSig is GenericInstSig gis && gis.GenericType?.TypeDefOrRef is TypeDef gtd && IsOwn(gtd) => gtd,
			_ => null,
		};

		MethodDef? ResolveOwn(IMethod method) => method switch {
			MethodDef md => IsOwn(md.DeclaringType) ? md : null,
			MemberRef mr when GetOwnTypeDef(mr.DeclaringType) is not null => mr.ResolveMethod(),
			_ => null,
		};

		FieldDef? ResolveOwn(IField field) => field switch {
			FieldDef fd => IsOwn(fd.DeclaringType) ? fd : null,
			MemberRef mr when GetOwnTypeDef(mr.DeclaringType) is not null => mr.ResolveField(),
			_ => null,
		};

		// The module's own type (not generic), or null if it's another assembly's
		TypeDef? GetOwnType(TypeSig sig) {
			sig = sig.RemovePinnedAndModifiers();
			if (sig is TypeDefOrRefSig tdrs && tdrs.TypeDefOrRef is TypeDef td && IsOwn(td))
				return td;
			if (sig is GenericInstSig gis && gis.GenericType?.TypeDefOrRef is TypeDef gtd && IsOwn(gtd))
				throw new EmulatorException($"Generic type {gtd} isn't supported");
			return null;
		}

		void EnsureStaticConstructor(TypeDef? type) {
			if (type is null || !IsOwn(type))
				return;
			if (cctorState.TryGetValue(type, out var error)) {
				if (error is not null && error.Length > 0)
					throw new EmulatorException(error);
				// null: done, "": running (a static constructor that uses its own type)
				return;
			}
			cctorState[type] = string.Empty;
			var cctor = type.FindStaticConstructor();
			if (cctor is null || cctor.Body is null) {
				cctorState[type] = null;
				return;
			}
			// A static constructor (eg. one that decompresses all the strings) gets its own instruction limit
			var savedLimit = stepLimit;
			var savedSteps = steps;
			steps = 0;
			stepLimit = Math.Max(stepLimit, StaticConstructorSteps);
			try {
				Execute(cctor, default, Array.Empty<object?>());
				cctorState[type] = null;
			}
			catch (EmulatorException ex) {
				var message = $"Static constructor of {type}: {ex.Message}";
				cctorState[type] = message;
				throw new EmulatorException(message);
			}
			finally {
				stepLimit = savedLimit;
				steps = savedSteps;
			}
		}

		object? LoadField(Dictionary<FieldDef, object?> fields, FieldDef field) {
			if (!fields.TryGetValue(field, out var value)) {
				value = Default(field.FieldType);
				fields[field] = value;
			}
			return value;
		}

		object? Default(TypeSig sig) {
			sig = sig.RemovePinnedAndModifiers();
			switch (sig.ElementType) {
			case ElementType.Boolean:
			case ElementType.Char:
			case ElementType.I1:
			case ElementType.U1:
			case ElementType.I2:
			case ElementType.U2:
			case ElementType.I4:
			case ElementType.U4:
				return 0;
			case ElementType.I8:
			case ElementType.U8:
			case ElementType.I:
			case ElementType.U:
			case ElementType.Ptr:
			case ElementType.FnPtr:
				return 0L;
			case ElementType.R4:
			case ElementType.R8:
				return 0.0;
			// Type operands are read with ToTypeSig(false) (resolving a reference to check if it's a struct would load its
			// assembly), so a .NET struct can be a ClassSig here
			case ElementType.ValueType:
			case ElementType.Class:
			case ElementType.GenericInst:
				var own = GetOwnType(sig);
				if (own is not null) {
					if (own.IsEnum)
						return Default(own.GetEnumUnderlyingType() ?? corLib.Int32);
					// A struct of the module: only its default value can be represented (used by eg. initobj on a local never read)
					return own.IsValueType ? new FakeObject(own) : null;
				}
				var type = ResolveType(sig, throwIfNotFound: false);
				if (type is not null && type.IsValueType && !type.ContainsGenericParameters) {
					try {
						return ToStack(Activator.CreateInstance(type));
					}
					catch {
					}
				}
				return null;
			default:
				return null;
			}
		}

		object? ToSlot(TypeSig sig, object? value) {
			sig = sig.RemovePinnedAndModifiers();
			switch (sig.ElementType) {
			case ElementType.Boolean:
			case ElementType.U1:
				return (int)(byte)AsInt(value);
			case ElementType.I1:
				return (int)(sbyte)AsInt(value);
			case ElementType.I2:
				return (int)(short)AsInt(value);
			case ElementType.U2:
			case ElementType.Char:
				return (int)(ushort)AsInt(value);
			case ElementType.I4:
			case ElementType.U4:
				return AsInt(value);
			case ElementType.I8:
			case ElementType.U8:
			case ElementType.I:
			case ElementType.U:
				return AsLong(value);
			case ElementType.R4:
				return (double)(float)AsDouble(value);
			case ElementType.R8:
				return AsDouble(value);
			case ElementType.ValueType:
				var own = GetOwnType(sig);
				if (own is not null && own.IsEnum)
					return ToSlot(own.GetEnumUnderlyingType() ?? corLib.Int32, value);
				return value;
			default:
				return value;
			}
		}

		static int AsInt(object? value) => value switch {
			int i => i,
			long l => (int)l,
			_ => throw new EmulatorException($"Expected an integer, not {Describe(value)}"),
		};

		static long AsLong(object? value) => value switch {
			long l => l,
			int i => i,
			_ => throw new EmulatorException($"Expected an integer, not {Describe(value)}"),
		};

		static double AsDouble(object? value) => value switch {
			double d => d,
			int i => i,
			long l => l,
			_ => throw new EmulatorException($"Expected a floating point number, not {Describe(value)}"),
		};

		static string Describe(object? value) => value is null ? "null" : value.GetType().Name;

		/// <summary>
		/// A .NET value as it's kept on the stack
		/// </summary>
		public static object? ToStack(object? value) {
			switch (value) {
			case null: return null;
			case int i: return i;
			case bool b: return b ? 1 : 0;
			case char c: return (int)c;
			case byte b: return (int)b;
			case sbyte b: return (int)b;
			case short s: return (int)s;
			case ushort s: return (int)s;
			case uint u: return unchecked((int)u);
			case long l: return l;
			case ulong u: return unchecked((long)u);
			case float f: return (double)f;
			case double d: return d;
			case IntPtr p: return p.ToInt64();
			case UIntPtr p: return unchecked((long)p.ToUInt64());
			case Enum e: return ToStack(Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType())));
			default: return value;
			}
		}

		/// <summary>
		/// A stack value converted to a .NET <paramref name="type"/> (for a call to a .NET method or an array element)
		/// </summary>
		public static object? FromStack(object? value, Type type) {
			if (type.IsEnum) {
				var underlying = FromStack(value, Enum.GetUnderlyingType(type));
				return underlying is null ? null : Enum.ToObject(type, underlying);
			}
			if (type.IsPrimitive) {
				unchecked {
					switch (Type.GetTypeCode(type)) {
					case TypeCode.Boolean: return value is double d0 ? d0 != 0 : AsLong(value) != 0;
					case TypeCode.Char: return (char)AsLong(value);
					case TypeCode.SByte: return (sbyte)AsLong(value);
					case TypeCode.Byte: return (byte)AsLong(value);
					case TypeCode.Int16: return (short)AsLong(value);
					case TypeCode.UInt16: return (ushort)AsLong(value);
					case TypeCode.Int32: return (int)AsLong(value);
					case TypeCode.UInt32: return (uint)AsLong(value);
					case TypeCode.Int64: return AsLong(value);
					case TypeCode.UInt64: return value is int i64 ? (ulong)(uint)i64 : (ulong)AsLong(value);
					case TypeCode.Single: return (float)AsDouble(value);
					case TypeCode.Double: return AsDouble(value);
					}
					if (type == typeof(IntPtr))
						return new IntPtr(AsLong(value));
					if (type == typeof(UIntPtr))
						return new UIntPtr((ulong)AsLong(value));
				}
			}
			if (value is null) {
				if (type.IsValueType)
					throw new EmulatorException($"null for a {type.Name}");
				return null;
			}
			if (value is FakeObject || value is FakeAssembly || value is FakeType || value is FakeTypeHandle || value is FakeFieldHandle || value is Ref)
				throw new EmulatorException($"An object of the assembly can't be passed to {type.FullName}");
			if (!type.IsInstanceOfType(value))
				throw new EmulatorException($"A {value.GetType().Name} isn't a {type.Name}");
			return value;
		}

		// A type of another assembly (the module's own types return null)
		Type? ResolveType(TypeSig sig, bool throwIfNotFound) {
			sig = sig.RemovePinnedAndModifiers();
			Type? type;
			switch (sig) {
			case SZArraySig sz:
				var elem = GetOwnType(sz.Next) is TypeDef ownElem ? (ownElem.IsEnum ? ResolveEnum(ownElem) : typeof(object)) : ResolveType(sz.Next, throwIfNotFound);
				return elem?.MakeArrayType();
			case ByRefSig br:
				return ResolveType(br.Next, throwIfNotFound)?.MakeByRefType();
			case GenericInstSig gis:
				var def = gis.GenericType is null ? null : ResolveType(gis.GenericType, throwIfNotFound);
				if (def is null)
					return null;
				var args = new Type[gis.GenericArguments.Count];
				for (int i = 0; i < args.Length; i++) {
					var arg = ResolveType(gis.GenericArguments[i], throwIfNotFound);
					if (arg is null) {
						if (throwIfNotFound)
							throw new EmulatorException($"Can't use {gis.GenericArguments[i]} as a generic argument of a .NET type");
						return null;
					}
					args[i] = arg;
				}
				try {
					return def.MakeGenericType(args);
				}
				catch (ArgumentException) {
					return null;
				}
			case TypeDefOrRefSig tdrs:
				if (tdrs.TypeDefOrRef is TypeDef td && IsOwn(td))
					return td.IsEnum ? ResolveEnum(td) : null;
				type = framework.FindType(FrameworkCalls.GetReflectionFullName(tdrs.TypeDefOrRef));
				if (type is null && throwIfNotFound)
					throw new EmulatorException($"Type {tdrs.TypeDefOrRef.FullName} isn't available");
				return type;
			default:
				if (throwIfNotFound)
					throw new EmulatorException($"Type {sig} isn't supported");
				return null;
			}
		}

		Type ResolveEnum(TypeDef type) => ResolveType(type.GetEnumUnderlyingType() ?? corLib.Int32, true)!;

		object? Pop(List<object?> stack) {
			if (stack.Count == 0)
				throw new EmulatorException("Stack is empty");
			var value = stack[stack.Count - 1];
			stack.RemoveAt(stack.Count - 1);
			return value;
		}

		object? Run(Frame frame, int start, bool inFinally) {
			var instrs = frame.Body.Instructions;
			var stack = new List<object?>();
			int i = start;
			while (true) {
				if (++steps > stepLimit)
					throw new EmulatorException($"Stopped after {stepLimit:N0} instructions");
				if ((uint)i >= (uint)instrs.Count)
					throw new EmulatorException("Ran past the end of the method");
				var instr = instrs[i];
				int next = i + 1;
				object? a, b;
				switch (instr.OpCode.Code) {
				case Code.Nop:
				case Code.Break:
				case Code.Volatile:
				case Code.Unaligned:
				case Code.Tailcall:
				case Code.Readonly:
				case Code.Constrained:
					break;

				case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3:
				case Code.Ldarg: case Code.Ldarg_S:
					stack.Add(frame.Args[GetParameterIndex(frame, instr)]);
					break;
				case Code.Ldarga: case Code.Ldarga_S: {
					int index = GetParameterIndex(frame, instr);
					stack.Add(new SlotRef(this, frame.Args, index, frame.Generic.Substitute(frame.Method.Parameters[index].Type)));
					break;
				}
				case Code.Starg: case Code.Starg_S: {
					int index = GetParameterIndex(frame, instr);
					frame.Args[index] = ToSlot(frame.Generic.Substitute(frame.Method.Parameters[index].Type), Pop(stack));
					break;
				}
				case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
				case Code.Ldloc: case Code.Ldloc_S:
					stack.Add(frame.Locals[instr.GetLocal(frame.Body.Variables).Index]);
					break;
				case Code.Ldloca: case Code.Ldloca_S: {
					var local = instr.GetLocal(frame.Body.Variables);
					stack.Add(new SlotRef(this, frame.Locals, local.Index, frame.Generic.Substitute(local.Type)));
					break;
				}
				case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
				case Code.Stloc: case Code.Stloc_S: {
					var local = instr.GetLocal(frame.Body.Variables);
					frame.Locals[local.Index] = ToSlot(frame.Generic.Substitute(local.Type), Pop(stack));
					break;
				}

				case Code.Ldnull: stack.Add(null); break;
				case Code.Ldc_I4_M1: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: case Code.Ldc_I4_3:
				case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8:
				case Code.Ldc_I4_S: case Code.Ldc_I4:
					stack.Add(instr.GetLdcI4Value());
					break;
				case Code.Ldc_I8: stack.Add((long)instr.Operand); break;
				case Code.Ldc_R4: stack.Add((double)(float)instr.Operand); break;
				case Code.Ldc_R8: stack.Add((double)instr.Operand); break;
				case Code.Ldstr: stack.Add((string)instr.Operand); break;

				case Code.Dup:
					a = Pop(stack);
					stack.Add(a);
					stack.Add(a);
					break;
				case Code.Pop: Pop(stack); break;

				case Code.Ret:
					if (inFinally)
						throw new EmulatorException("ret in a finally block");
					return frame.Method.MethodSig.RetType.RemovePinnedAndModifiers().ElementType == ElementType.Void ? null : Pop(stack);

				case Code.Br: case Code.Br_S:
					next = frame.Index[(Instruction)instr.Operand];
					break;
				case Code.Brfalse: case Code.Brfalse_S:
					if (!IsTrue(Pop(stack)))
						next = frame.Index[(Instruction)instr.Operand];
					break;
				case Code.Brtrue: case Code.Brtrue_S:
					if (IsTrue(Pop(stack)))
						next = frame.Index[(Instruction)instr.Operand];
					break;
				case Code.Beq: case Code.Beq_S:
				case Code.Bne_Un: case Code.Bne_Un_S:
				case Code.Bge: case Code.Bge_S: case Code.Bge_Un: case Code.Bge_Un_S:
				case Code.Bgt: case Code.Bgt_S: case Code.Bgt_Un: case Code.Bgt_Un_S:
				case Code.Ble: case Code.Ble_S: case Code.Ble_Un: case Code.Ble_Un_S:
				case Code.Blt: case Code.Blt_S: case Code.Blt_Un: case Code.Blt_Un_S:
					b = Pop(stack);
					a = Pop(stack);
					if (Branch(instr.OpCode.Code, a, b))
						next = frame.Index[(Instruction)instr.Operand];
					break;
				case Code.Switch: {
					var targets = (IList<Instruction>)instr.Operand;
					var index = AsInt(Pop(stack));
					if ((uint)index < (uint)targets.Count)
						next = frame.Index[targets[index]];
					break;
				}

				case Code.Ceq:
					b = Pop(stack);
					a = Pop(stack);
					stack.Add(Equal(a, b) ? 1 : 0);
					break;
				case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un:
					b = Pop(stack);
					a = Pop(stack);
					stack.Add(Compare(instr.OpCode.Code, a, b) ? 1 : 0);
					break;

				case Code.Add: case Code.Sub: case Code.Mul: case Code.Div: case Code.Div_Un: case Code.Rem: case Code.Rem_Un:
				case Code.And: case Code.Or: case Code.Xor: case Code.Shl: case Code.Shr: case Code.Shr_Un:
				case Code.Add_Ovf: case Code.Add_Ovf_Un: case Code.Sub_Ovf: case Code.Sub_Ovf_Un: case Code.Mul_Ovf: case Code.Mul_Ovf_Un:
					b = Pop(stack);
					a = Pop(stack);
					stack.Add(Arithmetic(instr.OpCode.Code, a, b));
					break;
				case Code.Neg:
					a = Pop(stack);
					stack.Add(a switch { int x => unchecked(-x), long x => unchecked(-x), double x => -x, _ => throw new EmulatorException($"neg of {Describe(a)}") });
					break;
				case Code.Not:
					a = Pop(stack);
					stack.Add(a switch { int x => ~x, long x => ~x, _ => throw new EmulatorException($"not of {Describe(a)}") });
					break;

				case Code.Conv_I1: case Code.Conv_I2: case Code.Conv_I4: case Code.Conv_I8: case Code.Conv_I:
				case Code.Conv_U1: case Code.Conv_U2: case Code.Conv_U4: case Code.Conv_U8: case Code.Conv_U:
				case Code.Conv_R4: case Code.Conv_R8: case Code.Conv_R_Un:
				case Code.Conv_Ovf_I1: case Code.Conv_Ovf_I2: case Code.Conv_Ovf_I4: case Code.Conv_Ovf_I8: case Code.Conv_Ovf_I:
				case Code.Conv_Ovf_U1: case Code.Conv_Ovf_U2: case Code.Conv_Ovf_U4: case Code.Conv_Ovf_U8: case Code.Conv_Ovf_U:
				case Code.Conv_Ovf_I1_Un: case Code.Conv_Ovf_I2_Un: case Code.Conv_Ovf_I4_Un: case Code.Conv_Ovf_I8_Un: case Code.Conv_Ovf_I_Un:
				case Code.Conv_Ovf_U1_Un: case Code.Conv_Ovf_U2_Un: case Code.Conv_Ovf_U4_Un: case Code.Conv_Ovf_U8_Un: case Code.Conv_Ovf_U_Un:
					stack.Add(Conversion(instr.OpCode.Code, Pop(stack)));
					break;

				case Code.Ldind_I1: case Code.Ldind_U1: case Code.Ldind_I2: case Code.Ldind_U2: case Code.Ldind_I4: case Code.Ldind_U4:
				case Code.Ldind_I8: case Code.Ldind_I: case Code.Ldind_R4: case Code.Ldind_R8: case Code.Ldind_Ref:
					stack.Add(AsRef(Pop(stack)).Get());
					break;
				case Code.Ldobj:
					stack.Add(AsRef(Pop(stack)).Get());
					break;
				case Code.Stind_I1: case Code.Stind_I2: case Code.Stind_I4: case Code.Stind_I8: case Code.Stind_I:
				case Code.Stind_R4: case Code.Stind_R8: case Code.Stind_Ref: case Code.Stobj:
					a = Pop(stack);
					AsRef(Pop(stack)).Set(a);
					break;
				case Code.Initobj: {
					var r = AsRef(Pop(stack));
					r.Set(Default(frame.Generic.Substitute(((ITypeDefOrRef)instr.Operand).ToTypeSig(false))));
					break;
				}

				case Code.Ldsfld: case Code.Ldsflda: case Code.Stsfld:
					StaticField(frame, instr, stack);
					break;
				case Code.Ldfld: case Code.Ldflda: case Code.Stfld:
					InstanceField(frame, instr, stack);
					break;

				case Code.Newarr: {
					var length = AsLong(Pop(stack));
					if (length < 0 || length > MaxArrayLength)
						throw new EmulatorException($"Array length {length}");
					var elemSig = frame.Generic.Substitute(((ITypeDefOrRef)instr.Operand).ToTypeSig(false));
					Type elemType;
					var own = GetOwnType(elemSig);
					if (own is not null)
						elemType = own.IsEnum ? ResolveEnum(own) : own.IsValueType ? throw new EmulatorException($"Arrays of {own} aren't supported") : typeof(object);
					else
						elemType = ResolveType(elemSig, true)!;
					stack.Add(Array.CreateInstance(elemType, (int)length));
					break;
				}
				case Code.Ldlen:
					stack.Add((long)AsArray(Pop(stack)).Length);
					break;
				case Code.Ldelema: {
					var index = AsLong(Pop(stack));
					var array = AsArray(Pop(stack));
					CheckIndex(array, index);
					stack.Add(new ArrayRef(array, (int)index));
					break;
				}
				case Code.Ldelem_I1: case Code.Ldelem_U1: case Code.Ldelem_I2: case Code.Ldelem_U2: case Code.Ldelem_I4: case Code.Ldelem_U4:
				case Code.Ldelem_I8: case Code.Ldelem_I: case Code.Ldelem_R4: case Code.Ldelem_R8: case Code.Ldelem_Ref: case Code.Ldelem: {
					var index = AsLong(Pop(stack));
					var array = AsArray(Pop(stack));
					CheckIndex(array, index);
					stack.Add(LoadElement(instr.OpCode.Code, array, (int)index));
					break;
				}
				case Code.Stelem_I1: case Code.Stelem_I2: case Code.Stelem_I4: case Code.Stelem_I8: case Code.Stelem_I:
				case Code.Stelem_R4: case Code.Stelem_R8: case Code.Stelem_Ref: case Code.Stelem: {
					var value = Pop(stack);
					var index = AsLong(Pop(stack));
					var array = AsArray(Pop(stack));
					CheckIndex(array, index);
					StoreElement(array, (int)index, value);
					break;
				}

				case Code.Box: {
					var sig = frame.Generic.Substitute(((ITypeDefOrRef)instr.Operand).ToTypeSig(false));
					a = Pop(stack);
					var own = GetOwnType(sig);
					if (own is not null && !own.IsEnum) {
						stack.Add(a);
						break;
					}
					var type = ResolveType(sig, true)!;
					stack.Add(type.IsValueType ? FromStack(a, type) : a);
					break;
				}
				case Code.Unbox_Any: {
					var sig = frame.Generic.Substitute(((ITypeDefOrRef)instr.Operand).ToTypeSig(false));
					a = Pop(stack);
					var own = GetOwnType(sig);
					if (own is not null && !own.IsEnum) {
						stack.Add(a);
						break;
					}
					var type = ResolveType(sig, true)!;
					if (type.IsValueType) {
						if (a is null)
							throw new EmulatorException("Unboxing null");
						stack.Add(ToStack(a));
					}
					else
						stack.Add(Cast(a, sig, true));
					break;
				}
				case Code.Castclass:
					stack.Add(Cast(Pop(stack), frame.Generic.Substitute(((ITypeDefOrRef)instr.Operand).ToTypeSig(false)), true));
					break;
				case Code.Isinst:
					stack.Add(Cast(Pop(stack), frame.Generic.Substitute(((ITypeDefOrRef)instr.Operand).ToTypeSig(false)), false));
					break;

				case Code.Ldtoken:
					stack.Add(LoadToken(frame, instr.Operand));
					break;
				case Code.Sizeof: {
					var type = ResolveType(frame.Generic.Substitute(((ITypeDefOrRef)instr.Operand).ToTypeSig(false)), true)!;
					if (!type.IsPrimitive)
						throw new EmulatorException($"sizeof {type.Name}");
					stack.Add(System.Runtime.InteropServices.Marshal.SizeOf(type == typeof(char) ? typeof(ushort) : type == typeof(bool) ? typeof(byte) : type));
					break;
				}

				case Code.Call: case Code.Callvirt:
					Call(frame, (IMethod)instr.Operand, instr.OpCode.Code == Code.Callvirt, stack, isNewobj: false);
					break;
				case Code.Newobj:
					Call(frame, (IMethod)instr.Operand, false, stack, isNewobj: true);
					break;

				case Code.Leave: case Code.Leave_S: {
					var target = (Instruction)instr.Operand;
					int targetIndex = frame.Index[target];
					stack.Clear();
					RunFinallyBlocks(frame, i, targetIndex);
					next = targetIndex;
					break;
				}
				case Code.Endfinally:
					if (!inFinally)
						throw new EmulatorException("endfinally outside of a finally block");
					return null;

				case Code.Throw:
					a = Pop(stack);
					throw new EmulatorException($"The code throws {(a is FakeObject fo ? fo.Type.FullName : Describe(a))}");
				default:
					throw new EmulatorException($"{instr.OpCode.Name} isn't supported");
				}
				i = next;
			}
		}

		static int GetParameterIndex(Frame frame, Instruction instr) {
			var p = instr.GetParameter(frame.Method.Parameters);
			if (p is null)
				throw new EmulatorException("Invalid argument");
			return p.Index;
		}

		void RunFinallyBlocks(Frame frame, int from, int to) {
			foreach (var eh in frame.Body.ExceptionHandlers) {
				if (eh.HandlerType != ExceptionHandlerType.Finally || eh.TryStart is null || eh.HandlerStart is null)
					continue;
				int tryStart = frame.Index[eh.TryStart];
				int tryEnd = eh.TryEnd is null ? frame.Body.Instructions.Count : frame.Index[eh.TryEnd];
				bool fromInside = from >= tryStart && from < tryEnd;
				bool toInside = to >= tryStart && to < tryEnd;
				if (fromInside && !toInside)
					Run(frame, frame.Index[eh.HandlerStart], true);
			}
		}

		static bool IsTrue(object? value) => value switch {
			null => false,
			int i => i != 0,
			long l => l != 0,
			double d => d != 0,
			_ => true,
		};

		static bool Equal(object? a, object? b) {
			switch (a) {
			case int x when b is int y: return x == y;
			case double x when b is double y: return x == y;
			case int or long when b is int or long: return AsLong(a) == AsLong(b);
			}
			if (a is ValueType || b is ValueType)
				return Equals(a, b);
			return ReferenceEquals(a, b);
		}

		static bool Compare(Code code, object? a, object? b) {
			bool unsigned = code == Code.Cgt_Un || code == Code.Clt_Un;
			bool greater = code == Code.Cgt || code == Code.Cgt_Un;
			int result;
			if (a is double || b is double) {
				double x = AsDouble(a), y = AsDouble(b);
				if (double.IsNaN(x) || double.IsNaN(y))
					return unsigned;
				result = x.CompareTo(y);
			}
			else if (a is int ia && b is int ib)
				result = unsigned ? ((uint)ia).CompareTo((uint)ib) : ia.CompareTo(ib);
			else if ((a is int || a is long) && (b is int || b is long))
				result = unsigned ? ((ulong)AsLong(a)).CompareTo((ulong)AsLong(b)) : AsLong(a).CompareTo(AsLong(b));
			else if (unsigned) {
				// x != null is compiled to cgt.un
				result = (a is null ? 0 : 1).CompareTo(b is null ? 0 : 1);
			}
			else
				throw new EmulatorException($"Can't compare {Describe(a)} and {Describe(b)}");
			return greater ? result > 0 : result < 0;
		}

		static bool Branch(Code code, object? a, object? b) {
			switch (code) {
			case Code.Beq: case Code.Beq_S: return Equal(a, b);
			case Code.Bne_Un: case Code.Bne_Un_S:
				if (a is double x && b is double y)
					return !(x == y);
				return !Equal(a, b);
			case Code.Bge: case Code.Bge_S: return IsNaN(a, b) ? false : !Compare(Code.Clt, a, b);
			case Code.Bge_Un: case Code.Bge_Un_S: return IsNaN(a, b) || !Compare(Code.Clt_Un, a, b);
			case Code.Bgt: case Code.Bgt_S: return Compare(Code.Cgt, a, b);
			case Code.Bgt_Un: case Code.Bgt_Un_S: return Compare(Code.Cgt_Un, a, b);
			case Code.Ble: case Code.Ble_S: return IsNaN(a, b) ? false : !Compare(Code.Cgt, a, b);
			case Code.Ble_Un: case Code.Ble_Un_S: return IsNaN(a, b) || !Compare(Code.Cgt_Un, a, b);
			case Code.Blt: case Code.Blt_S: return Compare(Code.Clt, a, b);
			case Code.Blt_Un: case Code.Blt_Un_S: return Compare(Code.Clt_Un, a, b);
			default: throw new InvalidOperationException();
			}
		}

		static bool IsNaN(object? a, object? b) => (a is double x && double.IsNaN(x)) || (b is double y && double.IsNaN(y));

		static object Arithmetic(Code code, object? a, object? b) {
			try {
				if (a is int x && b is int y) {
					switch (code) {
					case Code.Add: return unchecked(x + y);
					case Code.Sub: return unchecked(x - y);
					case Code.Mul: return unchecked(x * y);
					case Code.Div: return x == int.MinValue && y == -1 ? throw new EmulatorException("Overflow") : x / y;
					case Code.Div_Un: return (int)((uint)x / (uint)y);
					case Code.Rem: return x == int.MinValue && y == -1 ? 0 : x % y;
					case Code.Rem_Un: return (int)((uint)x % (uint)y);
					case Code.And: return x & y;
					case Code.Or: return x | y;
					case Code.Xor: return x ^ y;
					case Code.Shl: return x << y;
					case Code.Shr: return x >> y;
					case Code.Shr_Un: return (int)((uint)x >> y);
					case Code.Add_Ovf: return checked(x + y);
					case Code.Add_Ovf_Un: return (int)checked((uint)x + (uint)y);
					case Code.Sub_Ovf: return checked(x - y);
					case Code.Sub_Ovf_Un: return (int)checked((uint)x - (uint)y);
					case Code.Mul_Ovf: return checked(x * y);
					case Code.Mul_Ovf_Un: return (int)checked((uint)x * (uint)y);
					}
				}
				// Shifting an int64 by an int32
				if (a is long && b is int && (code == Code.Shl || code == Code.Shr || code == Code.Shr_Un)) {
					long l = (long)a;
					int s = (int)b;
					return code == Code.Shl ? l << s : code == Code.Shr ? l >> s : (long)((ulong)l >> s);
				}
				if ((a is int || a is long) && (b is int || b is long)) {
					long l = AsLong(a), r = AsLong(b);
					switch (code) {
					case Code.Add: return unchecked(l + r);
					case Code.Sub: return unchecked(l - r);
					case Code.Mul: return unchecked(l * r);
					case Code.Div: return l == long.MinValue && r == -1 ? throw new EmulatorException("Overflow") : l / r;
					case Code.Div_Un: return (long)((ulong)l / (ulong)r);
					case Code.Rem: return l == long.MinValue && r == -1 ? 0L : l % r;
					case Code.Rem_Un: return (long)((ulong)l % (ulong)r);
					case Code.And: return l & r;
					case Code.Or: return l | r;
					case Code.Xor: return l ^ r;
					case Code.Shl: return l << (int)r;
					case Code.Shr: return l >> (int)r;
					case Code.Shr_Un: return (long)((ulong)l >> (int)r);
					case Code.Add_Ovf: return checked(l + r);
					case Code.Add_Ovf_Un: return (long)checked((ulong)l + (ulong)r);
					case Code.Sub_Ovf: return checked(l - r);
					case Code.Sub_Ovf_Un: return (long)checked((ulong)l - (ulong)r);
					case Code.Mul_Ovf: return checked(l * r);
					case Code.Mul_Ovf_Un: return (long)checked((ulong)l * (ulong)r);
					}
				}
				if (a is double || b is double) {
					double l = AsDouble(a), r = AsDouble(b);
					switch (code) {
					case Code.Add: return l + r;
					case Code.Sub: return l - r;
					case Code.Mul: return l * r;
					case Code.Div: return l / r;
					case Code.Rem: return l % r;
					}
				}
			}
			catch (DivideByZeroException) {
				throw new EmulatorException("Division by zero");
			}
			catch (OverflowException) {
				throw new EmulatorException("Overflow");
			}
			throw new EmulatorException($"{code} of {Describe(a)} and {Describe(b)}");
		}

		static object Conversion(Code code, object? value) {
			try {
				checked {
					if (value is double d) {
						switch (code) {
						case Code.Conv_R4: return (double)(float)d;
						case Code.Conv_R8: case Code.Conv_R_Un: return d;
						case Code.Conv_I1: return (int)unchecked((sbyte)(long)d);
						case Code.Conv_I2: return (int)unchecked((short)(long)d);
						case Code.Conv_I4: return unchecked((int)(long)d);
						case Code.Conv_I8: case Code.Conv_I: return unchecked((long)d);
						case Code.Conv_U1: return (int)unchecked((byte)(long)d);
						case Code.Conv_U2: return (int)unchecked((ushort)(long)d);
						case Code.Conv_U4: return unchecked((int)(uint)(long)d);
						case Code.Conv_U8: case Code.Conv_U: return unchecked((long)(ulong)d);
						case Code.Conv_Ovf_I1: case Code.Conv_Ovf_I1_Un: return (int)(sbyte)d;
						case Code.Conv_Ovf_I2: case Code.Conv_Ovf_I2_Un: return (int)(short)d;
						case Code.Conv_Ovf_I4: case Code.Conv_Ovf_I4_Un: return (int)d;
						case Code.Conv_Ovf_I8: case Code.Conv_Ovf_I8_Un: case Code.Conv_Ovf_I: case Code.Conv_Ovf_I_Un: return (long)d;
						case Code.Conv_Ovf_U1: case Code.Conv_Ovf_U1_Un: return (int)(byte)d;
						case Code.Conv_Ovf_U2: case Code.Conv_Ovf_U2_Un: return (int)(ushort)d;
						case Code.Conv_Ovf_U4: case Code.Conv_Ovf_U4_Un: return unchecked((int)(uint)d);
						case Code.Conv_Ovf_U8: case Code.Conv_Ovf_U8_Un: case Code.Conv_Ovf_U: case Code.Conv_Ovf_U_Un: return unchecked((long)(ulong)d);
						}
					}
					long v = AsLong(value);
					// An int32 on the stack is unsigned for the *_Un conversions
					ulong u = value is int i32 ? (uint)i32 : unchecked((ulong)v);
					switch (code) {
					case Code.Conv_I1: return (int)unchecked((sbyte)v);
					case Code.Conv_I2: return (int)unchecked((short)v);
					case Code.Conv_I4: return unchecked((int)v);
					case Code.Conv_I8: case Code.Conv_I: return value is int ? v : v;
					case Code.Conv_U1: return (int)unchecked((byte)v);
					case Code.Conv_U2: return (int)unchecked((ushort)v);
					case Code.Conv_U4: return unchecked((int)(uint)v);
					case Code.Conv_U8: case Code.Conv_U: return value is int i ? (long)(uint)i : v;
					case Code.Conv_R4: return (double)(float)v;
					case Code.Conv_R8: return (double)v;
					case Code.Conv_R_Un: return (double)u;
					case Code.Conv_Ovf_I1: return (int)(sbyte)v;
					case Code.Conv_Ovf_I2: return (int)(short)v;
					case Code.Conv_Ovf_I4: return (int)v;
					case Code.Conv_Ovf_I8: case Code.Conv_Ovf_I: return v;
					case Code.Conv_Ovf_U1: return (int)(byte)v;
					case Code.Conv_Ovf_U2: return (int)(ushort)v;
					case Code.Conv_Ovf_U4: return unchecked((int)(uint)v);
					case Code.Conv_Ovf_U8: case Code.Conv_Ovf_U: return unchecked((long)(ulong)v);
					case Code.Conv_Ovf_I1_Un: return (int)(sbyte)u;
					case Code.Conv_Ovf_I2_Un: return (int)(short)u;
					case Code.Conv_Ovf_I4_Un: return (int)u;
					case Code.Conv_Ovf_I8_Un: case Code.Conv_Ovf_I_Un: return (long)u;
					case Code.Conv_Ovf_U1_Un: return (int)(byte)u;
					case Code.Conv_Ovf_U2_Un: return (int)(ushort)u;
					case Code.Conv_Ovf_U4_Un: return unchecked((int)(uint)u);
					case Code.Conv_Ovf_U8_Un: case Code.Conv_Ovf_U_Un: return unchecked((long)u);
					}
				}
			}
			catch (OverflowException) {
				throw new EmulatorException("Overflow");
			}
			throw new EmulatorException($"{code} of {Describe(value)}");
		}

		static Ref AsRef(object? value) => value as Ref ?? throw new EmulatorException($"Expected an address, not {Describe(value)}");
		static Array AsArray(object? value) => value as Array ?? throw new EmulatorException(value is null ? "Null array" : $"Expected an array, not {Describe(value)}");

		static void CheckIndex(Array array, long index) {
			if ((ulong)index >= (ulong)array.Length)
				throw new EmulatorException($"Index {index} is outside the array (length {array.Length})");
		}

		static object? LoadElement(Code code, Array array, int index) {
			switch (array) {
			case byte[] bytes: return code == Code.Ldelem_I1 ? (int)(sbyte)bytes[index] : (int)bytes[index];
			case sbyte[] sbytes: return code == Code.Ldelem_U1 ? (int)(byte)sbytes[index] : (int)sbytes[index];
			case char[] chars: return (int)chars[index];
			case int[] ints: return ints[index];
			case uint[] uints: return unchecked((int)uints[index]);
			case short[] shorts: return code == Code.Ldelem_U2 ? (int)(ushort)shorts[index] : (int)shorts[index];
			case ushort[] ushorts: return code == Code.Ldelem_I2 ? (int)(short)ushorts[index] : (int)ushorts[index];
			default: return ToStack(array.GetValue(index));
			}
		}

		static void StoreElement(Array array, int index, object? value) {
			switch (array) {
			case byte[] bytes: bytes[index] = unchecked((byte)AsLong(value)); return;
			case char[] chars: chars[index] = unchecked((char)AsLong(value)); return;
			case int[] ints: ints[index] = unchecked((int)AsLong(value)); return;
			case uint[] uints: uints[index] = unchecked((uint)AsLong(value)); return;
			case object[] objects when array.GetType().GetElementType() == typeof(object): objects[index] = value!; return;
			default: array.SetValue(FromStack(value, array.GetType().GetElementType()!), index); return;
			}
		}

		object? Cast(object? value, TypeSig sig, bool throwIfNot) {
			if (value is null)
				return null;
			bool ok;
			var own = GetOwnType(sig);
			if (own is not null)
				ok = value is FakeObject fo && IsSubclassOrSame(fo.Type, own);
			else if (value is FakeObject) {
				// An object of the module's type is an Object, and maybe an interface we can't check
				ok = sig.RemovePinnedAndModifiers().ElementType == ElementType.Object;
			}
			else {
				var type = ResolveType(sig, true)!;
				ok = type.IsInstanceOfType(value);
			}
			if (ok)
				return value;
			if (throwIfNot)
				throw new EmulatorException($"A {Describe(value)} isn't a {sig}");
			return null;
		}

		static bool IsSubclassOrSame(TypeDef? type, TypeDef baseType) {
			for (int i = 0; type is not null && i < 100; i++) {
				if (type == baseType)
					return true;
				if (baseType.IsInterface && type.Interfaces.Any(a => a.Interface == baseType))
					return true;
				type = type.BaseType as TypeDef;
			}
			return false;
		}

		object LoadToken(Frame frame, object operand) {
			switch (operand) {
			case FieldDef fd when IsOwn(fd.DeclaringType):
				return new FakeFieldHandle(fd);
			case ITypeDefOrRef tdr: {
				var sig = frame.Generic.Substitute(tdr.ToTypeSig(false));
				var own = GetOwnType(sig);
				if (own is not null)
					return new FakeTypeHandle(own);
				return ResolveType(sig, true)!.TypeHandle;
			}
			default:
				throw new EmulatorException($"ldtoken {operand}");
			}
		}

		FieldDef ResolveOwnField(IField field) {
			var fd = ResolveOwn(field);
			if (fd is null)
				throw new EmulatorException($"Field {field} can't be resolved");
			return fd;
		}

		void StaticField(Frame frame, Instruction instr, List<object?> stack) {
			var field = (IField)instr.Operand;
			var fd = ResolveOwn(field);
			if (fd is null) {
				// A static field of a .NET type, eg. String.Empty
				if (instr.OpCode.Code != Code.Ldsfld)
					throw new EmulatorException($"Can't change or take the address of {field}");
				var declaringType = ResolveType(field.DeclaringType.ToTypeSig(false), true)!;
				var fi = declaringType.GetField(field.Name, BindingFlags.Public | BindingFlags.Static);
				if (fi is null || !(fi.IsInitOnly || fi.IsLiteral) || !FrameworkCalls.IsAllowedType(declaringType))
					throw new EmulatorException($"Field {field} isn't available");
				stack.Add(ToStack(fi.GetValue(null)));
				return;
			}
			EnsureStaticConstructor(fd.DeclaringType);
			switch (instr.OpCode.Code) {
			case Code.Ldsfld: stack.Add(LoadField(staticFields, fd)); break;
			case Code.Ldsflda: stack.Add(new FieldRef(this, staticFields, fd)); break;
			default: staticFields[fd] = ToSlot(fd.FieldType, Pop(stack)); break;
			}
		}

		void InstanceField(Frame frame, Instruction instr, List<object?> stack) {
			var fd = ResolveOwnField((IField)instr.Operand);
			object? value = instr.OpCode.Code == Code.Stfld ? Pop(stack) : null;
			var obj = Pop(stack);
			if (obj is Ref r)
				obj = r.Get();
			if (obj is not FakeObject fo)
				throw new EmulatorException($"Field {fd.Name} of {Describe(obj)}");
			switch (instr.OpCode.Code) {
			case Code.Ldfld: stack.Add(LoadField(fo.Fields, fd)); break;
			case Code.Ldflda: stack.Add(new FieldRef(this, fo.Fields, fd)); break;
			default: fo.Fields[fd] = ToSlot(fd.FieldType, value); break;
			}
		}

		void Call(Frame frame, IMethod method, bool isVirtual, List<object?> stack, bool isNewobj) {
			IList<TypeSig>? methodGenArgs = null;
			IMethod target = method;
			if (method is MethodSpec spec) {
				methodGenArgs = spec.GenericInstMethodSig?.GenericArguments.Select(a => frame.Generic.Substitute(a)).ToList();
				target = spec.Method;
			}
			var sig = target.MethodSig ?? throw new EmulatorException($"{method} has no signature");
			int count = sig.Params.Count;
			bool hasThis = sig.HasThis && !isNewobj;
			if (stack.Count < count + (hasThis ? 1 : 0))
				throw new EmulatorException("Stack is empty");
			var args = new object?[count + (hasThis ? 1 : 0)];
			for (int i = args.Length - 1; i >= 0; i--)
				args[i] = Pop(stack);

			var md = ResolveOwn(target);
			var declaringSig = target.DeclaringType?.ToTypeSig(false);
			if (md is not null && IsOwn(md.DeclaringType)) {
				if (declaringSig is GenericInstSig)
					throw new EmulatorException($"Generic type {md.DeclaringType} isn't supported");
				CallOwn(frame, md, methodGenArgs, args, isVirtual, isNewobj, stack);
				return;
			}

			var typeName = target.DeclaringType?.FullName ?? string.Empty;
			if (TrySpecialCall(typeName, target.Name, args, hasThis, isNewobj, stack))
				return;

			// A .NET method, called for real
			var declaringType = ResolveType(frame.Generic.Substitute(declaringSig ?? throw new EmulatorException($"{method} has no type")), true)!;
			var typeArgs = declaringSig is GenericInstSig gis ? gis.GenericArguments.Select(a => frame.Generic.Substitute(a)).ToList() : null;
			var sigGeneric = new GenericArgs(typeArgs, methodGenArgs);
			var paramTypes = new Type[count];
			for (int i = 0; i < count; i++)
				paramTypes[i] = ResolveType(sigGeneric.Substitute(sig.Params[i]), true) ?? throw new EmulatorException($"Can't pass {sig.Params[i]} to {method}");
			Type? retType = null;
			if (target.Name == "op_Explicit" || target.Name == "op_Implicit")
				retType = ResolveType(sigGeneric.Substitute(sig.RetType), true);
			Type[]? genArgTypes = methodGenArgs?.Select(a => ResolveType(a, true) ?? throw new EmulatorException($"Can't use {a} as a generic argument")).ToArray();
			var mb = framework.FindMethod(declaringType, target.Name, paramTypes, retType, genArgTypes);
			if (mb is null)
				throw new EmulatorException($"{method.FullName} isn't available");
			if (!FrameworkCalls.IsAllowed(declaringType, mb))
				throw new EmulatorException($"{declaringType.FullName}.{target.Name} isn't allowed (only methods that compute something are run)");

			var ps = mb.GetParameters();
			var realArgs = new object?[count];
			var refs = new Ref?[count];
			for (int i = 0; i < count; i++) {
				var arg = args[i + (hasThis ? 1 : 0)];
				var pt = ps[i].ParameterType;
				if (pt.IsByRef) {
					var r = AsRef(arg);
					refs[i] = r;
					realArgs[i] = ps[i].IsOut ? (pt.GetElementType()!.IsValueType ? Activator.CreateInstance(pt.GetElementType()!) : null) : FromStack(r.Get(), pt.GetElementType()!);
				}
				else
					realArgs[i] = FromStack(arg, pt);
			}
			object? instance = null;
			if (hasThis) {
				instance = args[0];
				if (instance is Ref r) {
					// A method of a value type called on an address (eg. int.ToString()): the value is copied
					instance = FromStack(r.Get(), declaringType);
				}
				else if (declaringType.IsValueType && instance is not null && !declaringType.IsInstanceOfType(instance))
					instance = FromStack(instance, declaringType);
				if (instance is null)
					throw new EmulatorException($"{target.Name} called on null");
				if (instance is FakeObject || instance is FakeAssembly || instance is FakeType)
					throw new EmulatorException($"{declaringType.FullName}.{target.Name} on an object of the assembly");
			}
			object? result;
			try {
				if (mb is ConstructorInfo ci)
					result = ci.Invoke(realArgs);
				else
					result = mb.Invoke(instance, realArgs);
			}
			catch (TargetInvocationException ex) {
				throw new EmulatorException($"{declaringType.Name}.{target.Name} threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
			}
			catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is MemberAccessException) {
				throw new EmulatorException($"{declaringType.Name}.{target.Name}: {ex.Message}");
			}
			for (int i = 0; i < count; i++)
				refs[i]?.Set(ToStack(realArgs[i]));
			if (isNewobj)
				stack.Add(ToStack(result));
			else if (sig.RetType.RemovePinnedAndModifiers().ElementType != ElementType.Void)
				stack.Add(ToStack(result));
		}

		void CallOwn(Frame frame, MethodDef md, IList<TypeSig>? methodGenArgs, object?[] args, bool isVirtual, bool isNewobj, List<object?> stack) {
			if (isNewobj) {
				var type = md.DeclaringType;
				if (type.IsValueType)
					throw new EmulatorException($"Structs of the assembly ({type}) aren't supported");
				if (IsDelegate(type))
					throw new EmulatorException("Delegates aren't supported");
				EnsureStaticConstructor(type);
				var obj = new FakeObject(type);
				var ctorArgs = new object?[args.Length + 1];
				ctorArgs[0] = obj;
				Array.Copy(args, 0, ctorArgs, 1, args.Length);
				Execute(md, new GenericArgs(null, methodGenArgs), ToParameters(md, ctorArgs, methodGenArgs));
				stack.Add(obj);
				return;
			}
			var method = md;
			if (!md.IsStatic) {
				var thisObj = args[0] is Ref r ? r.Get() : args[0];
				if (thisObj is not FakeObject fo)
					throw new EmulatorException($"{md.Name} called on {Describe(thisObj)}");
				if (isVirtual && md.IsVirtual)
					method = FindOverride(fo.Type, md) ?? md;
				args[0] = fo;
			}
			else
				EnsureStaticConstructor(md.DeclaringType);
			if (method.Body is null)
				throw new EmulatorException($"{method} has no IL code ({(method.IsPinvokeImpl ? "native code" : method.IsAbstract ? "abstract" : "extern")})");
			var result = Execute(method, new GenericArgs(null, methodGenArgs), ToParameters(method, args, methodGenArgs));
			if (method.MethodSig.RetType.RemovePinnedAndModifiers().ElementType != ElementType.Void)
				stack.Add(result);
		}

		object?[] ToParameters(MethodDef method, object?[] args, IList<TypeSig>? methodGenArgs) {
			var generic = new GenericArgs(null, methodGenArgs);
			var ps = method.Parameters;
			for (int i = 0; i < args.Length && i < ps.Count; i++) {
				if (!ps[i].IsHiddenThisParameter)
					args[i] = ToSlot(generic.Substitute(ps[i].Type), args[i]);
			}
			return args;
		}

		static bool IsDelegate(TypeDef type) {
			var baseName = type.BaseType?.FullName;
			return baseName == "System.MulticastDelegate" || baseName == "System.Delegate";
		}

		static MethodDef? FindOverride(TypeDef type, MethodDef method) {
			for (var t = type; t is not null; t = t.BaseType as TypeDef) {
				foreach (var m in t.Methods) {
					if (m.IsVirtual && m.Body is not null && m.Overrides.Any(o => o.MethodDeclaration == method))
						return m;
				}
				var found = t.FindMethod(method.Name, method.MethodSig);
				if (found is not null && found.IsVirtual && found.Body is not null)
					return found;
				if (t == method.DeclaringType)
					break;
			}
			return null;
		}

		// Reflection and threading calls the emulator does itself, with the module's objects
		bool TrySpecialCall(string typeName, string name, object?[] args, bool hasThis, bool isNewobj, List<object?> stack) {
			switch (typeName) {
			case "System.Object":
				if (isNewobj)
					return false;
				if (name == ".ctor") {
					// base() of a class of the module
					return true;
				}
				if (args.Length > 0 && args[0] is FakeObject fo) {
					switch (name) {
					case "GetType": stack.Add(new FakeType(fo.Type)); return true;
					case "GetHashCode": stack.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fo)); return true;
					case "Equals": stack.Add(ReferenceEquals(fo, args[1]) ? 1 : 0); return true;
					case "ToString": stack.Add(fo.Type.ReflectionFullName); return true;
					}
				}
				return false;

			case "System.Runtime.CompilerServices.RuntimeHelpers":
				if (name == "InitializeArray" && args.Length == 2) {
					var array = AsArray(args[0]);
					if (args[1] is not FakeFieldHandle handle)
						throw new EmulatorException("InitializeArray with a field of another assembly");
					var data = handle.Field.InitialValue ?? throw new EmulatorException($"{handle.Field} has no initial data");
					int size = Buffer.ByteLength(array);
					if (data.Length < size)
						throw new EmulatorException($"{handle.Field} has {data.Length} bytes, the array needs {size}");
					Buffer.BlockCopy(data, 0, array, 0, size);
					return true;
				}
				return false;

			case "System.Type":
				if (name == "GetTypeFromHandle" && args.Length == 1 && args[0] is FakeTypeHandle th) {
					stack.Add(new FakeType(th.Type));
					return true;
				}
				if ((name == "op_Equality" || name == "op_Inequality") && args.Length == 2 && (args[0] is FakeType || args[1] is FakeType)) {
					bool eq = Equals(args[0], args[1]);
					stack.Add((name == "op_Equality") == eq ? 1 : 0);
					return true;
				}
				goto case "System.Reflection.MemberInfo";
			case "System.Reflection.MemberInfo":
				if (hasThis && args[0] is FakeType ft) {
					switch (name) {
					case "get_Assembly": stack.Add(fakeAssembly); return true;
					case "get_FullName": stack.Add(ft.Type.ReflectionFullName); return true;
					case "get_Name": stack.Add(ft.Type.Name.String); return true;
					case "get_Namespace": stack.Add(ft.Type.Namespace.String); return true;
					case "Equals": stack.Add(Equals(ft, args[1]) ? 1 : 0); return true;
					case "GetHashCode": stack.Add(ft.GetHashCode()); return true;
					}
					throw new EmulatorException($"Type.{name} isn't supported");
				}
				return false;

			case "System.Reflection.Assembly":
				switch (name) {
				case "GetExecutingAssembly":
				case "GetCallingAssembly":
				case "GetEntryAssembly":
					stack.Add(fakeAssembly);
					return true;
				case "op_Equality":
				case "op_Inequality":
					stack.Add((name == "op_Equality") == ReferenceEquals(args[0], args[1]) ? 1 : 0);
					return true;
				}
				if (hasThis && args[0] is FakeAssembly asm) {
					switch (name) {
					case "GetManifestResourceStream":
						var resName = args.Length == 2 ? args[1] as string : args.Length == 3 && args[1] is FakeType rt ? (rt.Type.Namespace.String.Length == 0 ? string.Empty : rt.Type.Namespace.String + ".") + args[2] : null;
						if (resName is null)
							throw new EmulatorException("GetManifestResourceStream with a type of another assembly");
						var res = asm.Module.Resources.FindEmbeddedResource(resName);
						stack.Add(res is null ? null : new MemoryStream(res.CreateReader().ToArray(), writable: false));
						return true;
					case "GetManifestResourceNames":
						stack.Add(asm.Module.Resources.OfType<EmbeddedResource>().Select(a => a.Name.String).ToArray());
						return true;
					case "get_FullName":
						stack.Add(asm.Module.Assembly?.FullName ?? asm.Module.Name.String);
						return true;
					case "Equals":
						stack.Add(ReferenceEquals(asm, args[1]) ? 1 : 0);
						return true;
					case "GetHashCode":
						stack.Add(0);
						return true;
					}
					throw new EmulatorException($"Assembly.{name} isn't supported");
				}
				return false;

			case "System.Threading.Monitor":
				// One thread: a lock does nothing
				if (name == "Enter" || name == "ReliableEnter") {
					if (args.Length == 2)
						AsRef(args[1]).Set(1);
					return true;
				}
				if (name == "Exit")
					return true;
				return false;

			case "System.Threading.Interlocked":
				if (args.Length > 0 && args[0] is Ref r) {
					var old = r.Get();
					switch (name) {
					case "CompareExchange" when args.Length == 3:
						if (Equal(old, args[2]))
							r.Set(args[1]);
						stack.Add(old);
						return true;
					case "Exchange" when args.Length == 2:
						r.Set(args[1]);
						stack.Add(old);
						return true;
					case "Increment" when args.Length == 1:
						r.Set(Arithmetic(Code.Add, old, old is long ? 1L : (object)1));
						stack.Add(r.Get());
						return true;
					case "Decrement" when args.Length == 1:
						r.Set(Arithmetic(Code.Sub, old, old is long ? 1L : (object)1));
						stack.Add(r.Get());
						return true;
					}
				}
				return false;
			}
			return false;
		}

		/// <summary>
		/// Runs a static constructor of &lt;Module&gt; that calls initializers: an initializer that fails (eg. anti-debugging
		/// code) is skipped instead of failing the static constructor, so the decryptor's own initializer still runs.
		/// </summary>
		public void RunModuleInitializer(long maxSteps) {
			var globalType = module.GlobalType;
			if (globalType is null || cctorState.ContainsKey(globalType))
				return;
			var cctor = globalType.FindStaticConstructor();
			if (cctor?.Body is null) {
				cctorState[globalType] = null;
				return;
			}
			cctorState[globalType] = string.Empty;
			// Only a static constructor that is just calls of methods without arguments, eg. ConfuserEx's
			var instrs = cctor.Body.Instructions;
			bool onlyCalls = cctor.Body.Variables.Count == 0 && instrs.All(a =>
				a.OpCode.Code == Code.Nop || a.OpCode.Code == Code.Ret ||
				(a.OpCode.Code == Code.Call && a.Operand is IMethod m && m.MethodSig is MethodSig s && s.Params.Count == 0 && !s.HasThis));
			if (!onlyCalls) {
				cctorState.Remove(globalType);
				return;
			}
			foreach (var instr in instrs) {
				if (instr.OpCode.Code != Code.Call)
					continue;
				var callee = (IMethod)instr.Operand;
				var md = ResolveOwn(callee);
				steps = 0;
				stepLimit = maxSteps;
				depth = 0;
				try {
					if (md is null || !IsOwn(md.DeclaringType))
						throw new EmulatorException($"{callee} is in another assembly");
					EnsureStaticConstructor(md.DeclaringType);
					var stack = new List<object?>();
					CallOwn(new Frame(cctor, cctor.Body, Array.Empty<object?>(), Array.Empty<object?>(), default, GetIndex(cctor.Body)), md, null, Array.Empty<object?>(), false, false, stack);
				}
				catch (EmulatorException ex) {
					SkippedInitializers.Add($"{callee.FullName}: {ex.Message}");
				}
			}
			cctorState[globalType] = null;
		}
	}
}
