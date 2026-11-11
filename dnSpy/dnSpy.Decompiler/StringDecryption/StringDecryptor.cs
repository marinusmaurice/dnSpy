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
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.StringDecryption {
	/// <summary>
	/// A method that looks like a string decryptor: static, returns a string, takes numbers or strings,
	/// and is called with constants
	/// </summary>
	public sealed class StringDecryptorInfo {
		/// <summary>The decryptor</summary>
		public MethodDef Method { get; }
		/// <summary>Number of calls with constant arguments</summary>
		public int CallSites { get; }

		internal StringDecryptorInfo(MethodDef method, int callSites) {
			Method = method;
			CallSites = callSites;
		}
	}

	/// <summary>
	/// A call of a decryptor that was replaced by its string
	/// </summary>
	public sealed class DecryptedString {
		/// <summary>The method with the call</summary>
		public MethodDef Method { get; }
		/// <summary>Offset of the call instruction</summary>
		public uint Offset { get; }
		/// <summary>The decryptor</summary>
		public MethodDef Decryptor { get; }
		/// <summary>The string</summary>
		public string Value { get; }

		internal DecryptedString(MethodDef method, uint offset, MethodDef decryptor, string value) {
			Method = method;
			Offset = offset;
			Decryptor = decryptor;
			Value = value;
		}
	}

	/// <summary>
	/// A call of a decryptor that couldn't be decrypted
	/// </summary>
	public sealed class StringDecryptionFailure {
		/// <summary>The method with the call</summary>
		public MethodDef Method { get; }
		/// <summary>Offset of the call instruction</summary>
		public uint Offset { get; }
		/// <summary>The decryptor</summary>
		public MethodDef Decryptor { get; }
		/// <summary>Why</summary>
		public string Reason { get; }

		internal StringDecryptionFailure(MethodDef method, uint offset, MethodDef decryptor, string reason) {
			Method = method;
			Offset = offset;
			Decryptor = decryptor;
			Reason = reason;
		}
	}

	/// <summary>
	/// What <see cref="StringDecryptor.Decrypt"/> did. The changes are made; <see cref="Undo"/> and <see cref="Redo"/> undo and redo them.
	/// </summary>
	public sealed class StringDecryptionResult {
		/// <summary>The calls that were replaced by their string</summary>
		public IReadOnlyList<DecryptedString> Decrypted { get; }
		/// <summary>The calls that weren't</summary>
		public IReadOnlyList<StringDecryptionFailure> Failed { get; }
		/// <summary>Calls in the static constructor of &lt;Module&gt; that failed and were skipped (eg. anti-debugging code)</summary>
		public IReadOnlyList<string> SkippedInitializers { get; }
		/// <summary>The methods whose code was changed</summary>
		public IReadOnlyList<MethodDef> ChangedMethods { get; }
		/// <summary>true if a call was replaced</summary>
		public bool HasChanges => edits.Length > 0;

		readonly Edit[] edits;
		readonly CilBody[] bodies;

		internal readonly struct Edit {
			public readonly Instruction Instruction;
			public readonly OpCode OldOpCode;
			public readonly object? OldOperand;
			public readonly OpCode NewOpCode;
			public readonly object? NewOperand;
			public Edit(Instruction instruction, OpCode newOpCode, object? newOperand) {
				Instruction = instruction;
				OldOpCode = instruction.OpCode;
				OldOperand = instruction.Operand;
				NewOpCode = newOpCode;
				NewOperand = newOperand;
			}
		}

		internal StringDecryptionResult(List<DecryptedString> decrypted, List<StringDecryptionFailure> failed, List<string> skipped, List<MethodDef> changed, List<Edit> edits) {
			Decrypted = decrypted;
			Failed = failed;
			SkippedInitializers = skipped;
			ChangedMethods = changed;
			this.edits = edits.ToArray();
			bodies = changed.Select(a => a.Body).Where(a => a is not null).Distinct().ToArray();
		}

		/// <summary>
		/// Puts the calls back
		/// </summary>
		public void Undo() {
			for (int i = edits.Length - 1; i >= 0; i--) {
				edits[i].Instruction.OpCode = edits[i].OldOpCode;
				edits[i].Instruction.Operand = edits[i].OldOperand;
			}
			UpdateOffsets();
		}

		/// <summary>
		/// Replaces the calls by their strings again
		/// </summary>
		public void Redo() => Apply();

		internal void Apply() {
			foreach (var edit in edits) {
				edit.Instruction.OpCode = edit.NewOpCode;
				edit.Instruction.Operand = edit.NewOperand;
			}
			UpdateOffsets();
		}

		void UpdateOffsets() {
			foreach (var body in bodies)
				body.UpdateInstructionOffsets();
		}
	}

	/// <summary>
	/// Finds string decryptors and replaces their calls by the strings they return. A decryptor runs in an emulator:
	/// none of the assembly's code runs, and the only .NET methods it may call are ones that compute something
	/// (strings, encodings, arrays, streams over memory, ciphers and hashes, collections), not files, processes,
	/// reflection or native code.
	/// </summary>
	public static class StringDecryptor {
		/// <summary>Most instructions one decryptor call may run</summary>
		public const long MaxStepsPerCall = 20_000_000;
		const int MaxParameters = 8;

		readonly struct CallSite {
			public readonly MethodDef Method;
			public readonly int CallIndex;
			public readonly int FirstArgIndex;
			public readonly MethodDef Decryptor;
			public readonly IList<TypeSig>? GenericArguments;
			public CallSite(MethodDef method, int callIndex, int firstArgIndex, MethodDef decryptor, IList<TypeSig>? genericArguments) {
				Method = method;
				CallIndex = callIndex;
				FirstArgIndex = firstArgIndex;
				Decryptor = decryptor;
				GenericArguments = genericArguments;
			}
		}

		/// <summary>
		/// Finds the methods that look like string decryptors, most called first.
		/// With <paramref name="only"/>, only those methods are checked (eg. one the user picked).
		/// </summary>
		public static IReadOnlyList<StringDecryptorInfo> FindDecryptors(ModuleDef module, IEnumerable<MethodDef>? only = null) {
			var filter = only is null ? null : new HashSet<MethodDef>(only);
			var counts = new Dictionary<MethodDef, int>();
			foreach (var site in FindCallSites(module, filter))
				counts[site.Decryptor] = counts.TryGetValue(site.Decryptor, out var n) ? n + 1 : 1;
			return counts.OrderByDescending(a => a.Value).ThenBy(a => a.Key.MDToken.Raw).Select(a => new StringDecryptorInfo(a.Key, a.Value)).ToList();
		}

		/// <summary>
		/// Can <paramref name="method"/> be a decryptor (static, returns a string, takes 1 to 8 numbers or strings)?
		/// </summary>
		public static bool CanBeDecryptor(MethodDef? method) {
			if (method is null || !method.IsStatic || method.Body is null || method.DeclaringType is null || method.DeclaringType.HasGenericParameters)
				return false;
			var sig = method.MethodSig;
			if (sig is null || sig.Params.Count == 0 || sig.Params.Count > MaxParameters || sig.ParamsAfterSentinel is not null)
				return false;
			var ret = sig.RetType.RemovePinnedAndModifiers();
			if (ret.ElementType == ElementType.String) {
				if (sig.GenParamCount != 0)
					return false;
			}
			// ConfuserEx: T Get<T>(uint id), called as Get<string>
			else if (!(ret is GenericMVar && sig.GenParamCount == 1))
				return false;
			foreach (var p in sig.Params) {
				if (!IsConstantType(p))
					return false;
			}
			return true;
		}

		static bool IsConstantType(TypeSig sig) {
			switch (sig.RemovePinnedAndModifiers().ElementType) {
			case ElementType.Boolean:
			case ElementType.Char:
			case ElementType.I1:
			case ElementType.U1:
			case ElementType.I2:
			case ElementType.U2:
			case ElementType.I4:
			case ElementType.U4:
			case ElementType.I8:
			case ElementType.U8:
			case ElementType.R4:
			case ElementType.R8:
			case ElementType.String:
				return true;
			default:
				return false;
			}
		}

		static bool IsConstant(Instruction instr) {
			switch (instr.OpCode.Code) {
			case Code.Ldc_I4_M1: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: case Code.Ldc_I4_3:
			case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8:
			case Code.Ldc_I4_S: case Code.Ldc_I4: case Code.Ldc_I8: case Code.Ldc_R4: case Code.Ldc_R8: case Code.Ldstr:
				return true;
			default:
				return false;
			}
		}

		static object? GetConstant(Instruction instr) {
			switch (instr.OpCode.Code) {
			case Code.Ldc_I8: return (long)instr.Operand;
			case Code.Ldc_R4: return (double)(float)instr.Operand;
			case Code.Ldc_R8: return (double)instr.Operand;
			case Code.Ldstr: return (string)instr.Operand;
			default: return instr.GetLdcI4Value();
			}
		}

		static IEnumerable<CallSite> FindCallSites(ModuleDef module, HashSet<MethodDef>? filter) {
			var eligible = new Dictionary<MethodDef, bool>();
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					var body = method.Body;
					if (body is null)
						continue;
					HashSet<Instruction>? targets = null;
					var instrs = body.Instructions;
					for (int i = 0; i < instrs.Count; i++) {
						var instr = instrs[i];
						if (instr.OpCode.Code != Code.Call)
							continue;
						MethodDef? decryptor;
						IList<TypeSig>? genArgs = null;
						if (instr.Operand is MethodSpec spec) {
							decryptor = spec.Method as MethodDef;
							genArgs = spec.GenericInstMethodSig?.GenericArguments;
							// Only Get<string>()
							if (genArgs is null || genArgs.Count != 1 || genArgs[0].RemovePinnedAndModifiers().ElementType != ElementType.String)
								continue;
						}
						else
							decryptor = instr.Operand as MethodDef;
						if (decryptor is null || decryptor.Module != module || decryptor == method)
							continue;
						if (filter is not null && !filter.Contains(decryptor))
							continue;
						if (!eligible.TryGetValue(decryptor, out var ok))
							eligible[decryptor] = ok = CanBeDecryptor(decryptor);
						if (!ok)
							continue;
						// A generic decryptor must be called as Get<string>, a normal one without generic arguments
						if ((genArgs is null) != (decryptor.MethodSig.GenParamCount == 0))
							continue;
						int count = decryptor.MethodSig.Params.Count;
						int first = i - count;
						if (first < 0)
							continue;
						bool constants = true;
						for (int k = first; k < i; k++) {
							if (!IsConstant(instrs[k])) {
								constants = false;
								break;
							}
						}
						if (!constants)
							continue;
						targets ??= GetTargets(body);
						bool jumpedInto = false;
						for (int k = first + 1; k <= i; k++) {
							if (targets.Contains(instrs[k])) {
								jumpedInto = true;
								break;
							}
						}
						if (jumpedInto)
							continue;
						yield return new CallSite(method, i, first, decryptor, genArgs);
					}
				}
			}
		}

		static HashSet<Instruction> GetTargets(CilBody body) {
			var targets = new HashSet<Instruction>();
			foreach (var instr in body.Instructions) {
				if (instr.Operand is Instruction target)
					targets.Add(target);
				else if (instr.Operand is IList<Instruction> list) {
					foreach (var t in list)
						targets.Add(t);
				}
			}
			foreach (var eh in body.ExceptionHandlers) {
				foreach (var t in new[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart }) {
					if (t is not null)
						targets.Add(t);
				}
			}
			return targets;
		}

		/// <summary>
		/// Replaces the calls of <paramref name="decryptors"/> that have constant arguments by the strings they return
		/// </summary>
		public static StringDecryptionResult Decrypt(ModuleDef module, IEnumerable<MethodDef> decryptors) {
			StringDecryptionResult? result = null;
			Exception? error = null;
			var set = new HashSet<MethodDef>(decryptors);
			// Nested calls in the emulator use the thread's stack
			var thread = new Thread(() => {
				try {
					result = DecryptCore(module, set);
				}
				catch (Exception ex) {
					error = ex;
				}
			}, 64 * 1024 * 1024);
			thread.Start();
			thread.Join();
			if (error is not null)
				throw new InvalidOperationException(error.Message, error);
			return result!;
		}

		static StringDecryptionResult DecryptCore(ModuleDef module, HashSet<MethodDef> decryptors) {
			var sites = FindCallSites(module, decryptors).ToList();
			var decrypted = new List<DecryptedString>();
			var failed = new List<StringDecryptionFailure>();
			var edits = new List<StringDecryptionResult.Edit>();
			var changed = new List<MethodDef>();
			var emulator = new ILEmulator(module, new FrameworkCalls());
			emulator.RunModuleInitializer(MaxStepsPerCall * 10);
			var cache = new Dictionary<string, (string? value, string? error)>(StringComparer.Ordinal);

			foreach (var site in sites) {
				var instrs = site.Method.Body.Instructions;
				var call = instrs[site.CallIndex];
				var ps = site.Decryptor.MethodSig.Params;
				var args = new object?[ps.Count];
				for (int k = 0; k < args.Length; k++)
					args[k] = GetConstant(instrs[site.FirstArgIndex + k]);
				var key = GetKey(site.Decryptor, args);
				if (!cache.TryGetValue(key, out var outcome)) {
					try {
						for (int k = 0; k < args.Length; k++)
							args[k] = emulator.ToParameter(ps[k], args[k]);
						var value = emulator.Call(site.Decryptor, site.GenericArguments, args, MaxStepsPerCall);
						outcome = value switch {
							string s => (s, null),
							null => (null, "It returned null"),
							_ => (null, $"It returned a {value.GetType().Name}, not a string"),
						};
					}
					catch (EmulatorException ex) {
						outcome = (null, ex.Message);
					}
					catch (Exception ex) when (ex is not OutOfMemoryException) {
						outcome = (null, $"{ex.GetType().Name}: {ex.Message}");
					}
					cache[key] = outcome;
				}
				if (outcome.value is null) {
					failed.Add(new StringDecryptionFailure(site.Method, call.Offset, site.Decryptor, outcome.error ?? string.Empty));
					continue;
				}
				decrypted.Add(new DecryptedString(site.Method, call.Offset, site.Decryptor, outcome.value));
				// The first argument becomes the string, the other arguments and the call nops (branches to the first stay valid)
				edits.Add(new StringDecryptionResult.Edit(instrs[site.FirstArgIndex], OpCodes.Ldstr, outcome.value));
				for (int k = site.FirstArgIndex + 1; k <= site.CallIndex; k++)
					edits.Add(new StringDecryptionResult.Edit(instrs[k], OpCodes.Nop, null));
				if (changed.Count == 0 || changed[changed.Count - 1] != site.Method)
					changed.Add(site.Method);
			}

			var result = new StringDecryptionResult(decrypted, failed, emulator.SkippedInitializers, changed.Distinct().ToList(), edits);
			result.Apply();
			return result;
		}

		static string GetKey(MethodDef decryptor, object?[] args) {
			var sb = new StringBuilder();
			sb.Append(decryptor.MDToken.Raw.ToString("X8", CultureInfo.InvariantCulture));
			foreach (var arg in args) {
				sb.Append('|');
				switch (arg) {
				case string s: sb.Append('s').Append(s.Length).Append(':').Append(s); break;
				case double d: sb.Append('d').Append(BitConverter.DoubleToInt64Bits(d)); break;
				case long l: sb.Append('l').Append(l); break;
				default: sb.Append('i').Append(Convert.ToString(arg, CultureInfo.InvariantCulture)); break;
				}
			}
			return sb.ToString();
		}
	}
}
