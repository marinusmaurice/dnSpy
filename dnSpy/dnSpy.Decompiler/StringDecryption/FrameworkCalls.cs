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
using System.Linq;
using System.Reflection;
using dnlib.DotNet;

namespace dnSpy.Decompiler.StringDecryption {
	/// <summary>
	/// The .NET types whose methods the emulator calls for real (in dnSpy's own runtime). Only types that compute
	/// something (strings, numbers, arrays, encodings, streams over memory, decompression, ciphers and hashes, collections)
	/// are allowed: nothing that touches files, the network, the registry, processes, reflection or native code.
	/// </summary>
	sealed class FrameworkCalls {
		// Every member of these types may be called
		static readonly HashSet<string> allowedTypes = new HashSet<string>(StringComparer.Ordinal) {
			"System.Object", "System.String", "System.Char", "System.Boolean", "System.Byte", "System.SByte",
			"System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
			"System.Single", "System.Double", "System.Decimal", "System.IntPtr", "System.UIntPtr",
			"System.Math", "System.Convert", "System.BitConverter", "System.Buffer", "System.Array",
			"System.RuntimeTypeHandle", "System.RuntimeFieldHandle", "System.IDisposable", "System.StringComparer",
			"System.StringComparison", "System.StringSplitOptions", "System.Globalization.NumberStyles",
			"System.Text.Encoding", "System.Text.UTF8Encoding", "System.Text.ASCIIEncoding", "System.Text.UnicodeEncoding",
			"System.Text.UTF32Encoding", "System.Text.UTF7Encoding", "System.Text.Decoder", "System.Text.Encoder",
			"System.Text.StringBuilder",
			"System.IO.Stream", "System.IO.MemoryStream", "System.IO.SeekOrigin",
			"System.IO.Compression.DeflateStream", "System.IO.Compression.GZipStream", "System.IO.Compression.CompressionMode",
			"System.Security.Cryptography.SymmetricAlgorithm", "System.Security.Cryptography.Aes",
			"System.Security.Cryptography.AesManaged", "System.Security.Cryptography.AesCryptoServiceProvider",
			"System.Security.Cryptography.Rijndael", "System.Security.Cryptography.RijndaelManaged",
			"System.Security.Cryptography.DES", "System.Security.Cryptography.DESCryptoServiceProvider",
			"System.Security.Cryptography.TripleDES", "System.Security.Cryptography.TripleDESCryptoServiceProvider",
			"System.Security.Cryptography.RC2", "System.Security.Cryptography.RC2CryptoServiceProvider",
			"System.Security.Cryptography.ICryptoTransform", "System.Security.Cryptography.CryptoStream",
			"System.Security.Cryptography.CryptoStreamMode", "System.Security.Cryptography.CipherMode",
			"System.Security.Cryptography.PaddingMode", "System.Security.Cryptography.HashAlgorithm",
			"System.Security.Cryptography.KeyedHashAlgorithm", "System.Security.Cryptography.HMAC",
			"System.Security.Cryptography.HMACMD5", "System.Security.Cryptography.HMACSHA1", "System.Security.Cryptography.HMACSHA256",
			"System.Security.Cryptography.MD5", "System.Security.Cryptography.MD5CryptoServiceProvider",
			"System.Security.Cryptography.SHA1", "System.Security.Cryptography.SHA1Managed", "System.Security.Cryptography.SHA1CryptoServiceProvider",
			"System.Security.Cryptography.SHA256", "System.Security.Cryptography.SHA256Managed", "System.Security.Cryptography.SHA256CryptoServiceProvider",
			"System.Security.Cryptography.SHA384", "System.Security.Cryptography.SHA512", "System.Security.Cryptography.HashAlgorithmName",
			"System.Security.Cryptography.DeriveBytes", "System.Security.Cryptography.Rfc2898DeriveBytes",
			"System.Security.Cryptography.PasswordDeriveBytes",
			"System.Collections.Generic.List`1", "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.KeyValuePair`2",
			"System.Collections.Generic.Queue`1", "System.Collections.Generic.Stack`1", "System.Collections.Generic.HashSet`1",
			"System.Collections.Generic.EqualityComparer`1", "System.Collections.Generic.IEqualityComparer`1",
			"System.Collections.Hashtable", "System.Collections.ArrayList",
		};

		// Types with only some members allowed. Readers and writers only over a stream (a string argument is a file name).
		static readonly HashSet<string> streamWrappers = new HashSet<string>(StringComparer.Ordinal) {
			"System.IO.BinaryReader", "System.IO.BinaryWriter", "System.IO.StreamReader", "System.IO.StreamWriter",
			"System.IO.TextReader", "System.IO.TextWriter",
		};
		static readonly HashSet<string> allowedTypeMembers = new HashSet<string>(StringComparer.Ordinal) {
			"GetTypeFromHandle", "op_Equality", "op_Inequality", "Equals", "GetHashCode", "get_FullName", "get_Name",
			"get_Namespace", "get_TypeHandle", "ToString",
		};
		static readonly HashSet<string> allowedCultureMembers = new HashSet<string>(StringComparer.Ordinal) {
			"get_InvariantCulture", "get_NumberFormat",
		};

		static readonly string[] assemblyNames = {
			"System.Runtime", "System.Private.CoreLib", "mscorlib", "System", "System.Core", "System.Collections",
			"System.Collections.NonGeneric", "System.Text.Encoding.Extensions", "System.IO.Compression",
			"System.Security.Cryptography", "System.Security.Cryptography.Algorithms", "System.Security.Cryptography.Primitives",
			"System.Security.Cryptography.Csp", "System.Runtime.Extensions", "System.Memory",
		};

		readonly Dictionary<string, Type?> typeCache = new Dictionary<string, Type?>(StringComparer.Ordinal);
		readonly Dictionary<string, MethodBase> methodCache = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
		readonly List<Assembly> assemblies = new List<Assembly>();

		public FrameworkCalls() {
			var seen = new HashSet<Assembly>();
			void Add(Assembly? asm) {
				if (asm is not null && seen.Add(asm))
					assemblies.Add(asm);
			}
			Add(typeof(object).Assembly);
			Add(typeof(Uri).Assembly);
			Add(typeof(System.Text.Encoding).Assembly);
			Add(typeof(System.Security.Cryptography.HashAlgorithm).Assembly);
			Add(typeof(System.Collections.Generic.HashSet<>).Assembly);
			foreach (var name in assemblyNames) {
				try {
					Add(Assembly.Load(new AssemblyName(name)));
				}
				catch {
				}
			}
		}

		public static string GetFullName(Type type) => type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition().FullName ?? type.Name : type.FullName ?? type.Name;

		/// <summary>
		/// Can <paramref name="member"/> (a member of <paramref name="declaringType"/>) be called for real?
		/// </summary>
		public static bool IsAllowed(Type declaringType, MethodBase member) {
			var name = GetFullName(declaringType);
			if (declaringType.IsArray)
				return true;
			if (allowedTypes.Contains(name))
				return true;
			if (streamWrappers.Contains(name)) {
				if (member is ConstructorInfo) {
					var ps = member.GetParameters();
					return ps.Length > 0 && typeof(System.IO.Stream).IsAssignableFrom(ps[0].ParameterType);
				}
				return true;
			}
			if (name == "System.Type" || name == "System.Reflection.MemberInfo" || name == "System.Reflection.TypeInfo")
				return allowedTypeMembers.Contains(member.Name);
			if (name == "System.Globalization.CultureInfo")
				return allowedCultureMembers.Contains(member.Name);
			if (name == "System.Environment")
				return member.Name == "get_NewLine";
			return false;
		}

		/// <summary>
		/// Can the read-only static fields of <paramref name="type"/> be read (eg. <c>String.Empty</c>)?
		/// </summary>
		public static bool IsAllowedType(Type type) => allowedTypes.Contains(GetFullName(type));

		/// <summary>
		/// Finds a .NET type by its full name (nested types with <c>+</c>) in dnSpy's own runtime
		/// </summary>
		public Type? FindType(string reflectionFullName) {
			if (typeCache.TryGetValue(reflectionFullName, out var type))
				return type;
			foreach (var asm in assemblies) {
				try {
					type = asm.GetType(reflectionFullName, false);
				}
				catch {
					type = null;
				}
				if (type is not null)
					break;
			}
			typeCache[reflectionFullName] = type;
			return type;
		}

		/// <summary>
		/// Finds the method or constructor <paramref name="name"/> with these parameter types.
		/// <paramref name="genericArguments"/> are the arguments of a generic method.
		/// </summary>
		public MethodBase? FindMethod(Type declaringType, string name, Type[] parameterTypes, Type? returnType, Type[]? genericArguments) {
			var key = declaringType.AssemblyQualifiedName + "::" + name + "(" + string.Join(",", parameterTypes.Select(a => a.AssemblyQualifiedName)) + ")" +
				(genericArguments is null ? string.Empty : "<" + string.Join(",", genericArguments.Select(a => a.AssemblyQualifiedName)) + ">") +
				returnType?.AssemblyQualifiedName;
			if (methodCache.TryGetValue(key, out var cached))
				return cached;
			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
			MethodBase? found = null;
			if (name == ".ctor")
				found = declaringType.GetConstructor(flags, null, parameterTypes, null);
			else if (genericArguments is null) {
				foreach (var m in declaringType.GetMethods(flags)) {
					if (m.Name != name || m.IsGenericMethodDefinition || !SameParameters(m, parameterTypes))
						continue;
					// op_Explicit and op_Implicit differ only in the return type
					if (returnType is not null && m.ReturnType != returnType)
						continue;
					found = m;
					break;
				}
			}
			else {
				foreach (var m in declaringType.GetMethods(flags)) {
					if (m.Name != name || !m.IsGenericMethodDefinition || m.GetGenericArguments().Length != genericArguments.Length)
						continue;
					MethodInfo inst;
					try {
						inst = m.MakeGenericMethod(genericArguments);
					}
					catch (ArgumentException) {
						continue;
					}
					if (SameParameters(inst, parameterTypes)) {
						found = inst;
						break;
					}
				}
			}
			// A method that doesn't exist in this runtime (eg. Array.Reverse<T>(T[]), new in .NET Core, when dnSpy runs on
			// .NET Framework): a non-generic overload whose parameters take the same arguments (Array.Reverse(Array))
			if (found is null && name != ".ctor") {
				foreach (var m in declaringType.GetMethods(flags)) {
					if (m.Name != name || m.IsGenericMethodDefinition || !AcceptsParameters(m, parameterTypes))
						continue;
					if (returnType is not null && m.ReturnType != returnType)
						continue;
					found = m;
					break;
				}
			}
			if (found is not null)
				methodCache[key] = found;
			return found;
		}

		static bool AcceptsParameters(MethodBase method, Type[] types) {
			var ps = method.GetParameters();
			if (ps.Length != types.Length)
				return false;
			for (int i = 0; i < ps.Length; i++) {
				var pt = ps[i].ParameterType;
				if (pt.IsByRef || types[i].IsByRef || types[i].IsValueType != pt.IsValueType || !pt.IsAssignableFrom(types[i]))
					return false;
			}
			return true;
		}

		static bool SameParameters(MethodBase method, Type[] types) {
			var ps = method.GetParameters();
			if (ps.Length != types.Length)
				return false;
			for (int i = 0; i < ps.Length; i++) {
				if (ps[i].ParameterType != types[i])
					return false;
			}
			return true;
		}

		/// <summary>
		/// The reflection name of a type reference (nested types with <c>+</c>), without generic arguments
		/// </summary>
		public static string GetReflectionFullName(ITypeDefOrRef type) => type.ReflectionFullName;
	}
}
