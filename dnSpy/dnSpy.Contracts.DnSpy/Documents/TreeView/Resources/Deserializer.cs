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
using System.IO;
using dnlib.DotNet;
#if NETFRAMEWORK
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
#else
using System.Formats.Nrbf;
#endif

namespace dnSpy.Contracts.Documents.TreeView.Resources {
	sealed class DeserializedDataInfo {
		public string Name { get; }
		public object? Value { get; }

		public DeserializedDataInfo(string name, object? value) {
			Name = name;
			Value = value;
		}
	}

	/// <summary>
	/// Deserializes data without loading the deserialized types. Only our types get "deserialized".
	/// This is only useful if it's eg. an image and it's stored in a byte[].
	/// </summary>
	static class Deserializer {
		sealed class TypeMatcher {
			readonly ModuleDef module;
			readonly ITypeDefOrRef type;

			public TypeMatcher(string asmName, string typeName) {
				module = new ModuleDefUser();
				type = TypeNameParser.ParseReflection(module, $"{typeName}, {asmName}", null);
			}

			public bool IsMatch(string assemblyName, string typeName) {
				var otherType = TypeNameParser.ParseReflection(module, $"{typeName}, {assemblyName}", null);
				if (string.IsNullOrEmpty(type.TypeName) || string.IsNullOrEmpty(otherType.TypeName))
					return false;
				if (!new SigComparer().Equals(type, otherType))
					return false;
				var flags = AssemblyNameComparerFlags.All & ~AssemblyNameComparerFlags.Version;
				return new AssemblyNameComparer(flags).Equals(type.DefinitionAssembly, otherType.DefinitionAssembly);
			}
		}

#if NETFRAMEWORK
		sealed class MyBinder : SerializationBinder {
			readonly TypeMatcher matcher;

			public MyBinder(string asmName, string typeName) => matcher = new TypeMatcher(asmName, typeName);

			public override Type BindToType(string assemblyName, string typeName) =>
				matcher.IsMatch(assemblyName, typeName) ? typeof(DeserializedType) : typeof(DontDeserializeType);
		}

		[Serializable]
		sealed class DeserializedType : ISerializable {
			public readonly Dictionary<string, DeserializedDataInfo> DeserializedDataInfos = new Dictionary<string, DeserializedDataInfo>(StringComparer.Ordinal);

			public DeserializedType(SerializationInfo info, StreamingContext context) {
				foreach (var c in info)
					DeserializedDataInfos[c.Name] = new DeserializedDataInfo(c.Name, c.Value);
			}

			public void GetObjectData(SerializationInfo info, StreamingContext context) => throw new NotImplementedException();
		}

		[Serializable]
		sealed class DontDeserializeType : ISerializable {
			public DontDeserializeType(SerializationInfo info, StreamingContext context) {
			}

			public void GetObjectData(SerializationInfo info, StreamingContext context) => throw new NotImplementedException();
		}

		public static Dictionary<string, DeserializedDataInfo> Deserialize(string asmName, string typeName, byte[] data) {
			var fmt = new BinaryFormatter();
			fmt.Binder = new MyBinder(asmName, typeName);
			var obj = fmt.Deserialize(new MemoryStream(data)) as DeserializedType;
			Debug2.Assert(obj is not null);
			if (obj is null)
				return new Dictionary<string, DeserializedDataInfo>();
			return obj.DeserializedDataInfos;
		}
#else
		// BinaryFormatter isn't available on .NET 9+. NrbfDecoder only parses the data, it never loads any types.
		public static Dictionary<string, DeserializedDataInfo> Deserialize(string asmName, string typeName, byte[] data) {
			var result = new Dictionary<string, DeserializedDataInfo>(StringComparer.Ordinal);
			var record = NrbfDecoder.DecodeClassRecord(new MemoryStream(data));
			var recordTypeName = record.TypeName;
			if (!new TypeMatcher(asmName, typeName).IsMatch(recordTypeName.AssemblyName?.FullName ?? string.Empty, recordTypeName.FullName))
				return result;
			foreach (var name in record.MemberNames) {
				object? value = record.GetRawValue(name) switch {
					SZArrayRecord<byte> bytes => bytes.GetArray(),
					SerializationRecord => null,
					var rawValue => rawValue,
				};
				result[name] = new DeserializedDataInfo(name, value);
			}
			return result;
		}
#endif
	}
}
