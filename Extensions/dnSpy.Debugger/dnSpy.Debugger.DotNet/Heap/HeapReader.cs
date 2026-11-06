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
using System.Text;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Debugger.DotNet.Metadata;

// Reads an object's fields for the Heap window. Only fields are read (no code runs in the process).
namespace dnSpy.Debugger.DotNet.Heap {
	sealed class HeapFieldInfo {
		public string Name { get; }
		public string Value { get; }
		public string TypeName { get; }
		/// <summary>The field's object (a non-null reference) to show its fields, or null</summary>
		public DbgDotNetValue? Object { get; }

		public HeapFieldInfo(string name, string value, string typeName, DbgDotNetValue? obj) {
			Name = name;
			Value = value;
			TypeName = typeName;
			Object = obj;
		}
	}

	static class HeapReader {
		const DmdBindingFlags FieldFlags = DmdBindingFlags.Instance | DmdBindingFlags.Public | DmdBindingFlags.NonPublic | DmdBindingFlags.DeclaredOnly;
		const int MaxStringLength = 200;

		public static string FormatTypeName(DmdType type) => (type.ToString() ?? string.Empty).Replace('+', '.');

		public static ulong GetAddress(DbgDotNetValue value) => value.IsNull ? 0 : value.GetRawAddressValue(onlyDataAddress: false)?.Address ?? 0;

		public static string FormatAddress(ulong address) => "0x" + address.ToString("X" + (address > uint.MaxValue ? "16" : "8"), CultureInfo.InvariantCulture);

		/// <summary>
		/// The instance fields, base class fields first
		/// </summary>
		public static List<DmdFieldInfo> GetFields(DmdType type) {
			var types = new List<DmdType>();
			for (var t = type; t is not null && t != t.AppDomain.System_Object; t = t.BaseType)
				types.Add(t);
			types.Reverse();
			var fields = new List<DmdFieldInfo>();
			foreach (var t in types)
				fields.AddRange(t.GetFields(FieldFlags));
			return fields;
		}

		/// <summary>
		/// Reads all instance fields of <paramref name="obj"/>
		/// </summary>
		public static List<HeapFieldInfo> ReadFields(DbgEvaluationInfo evalInfo, IDbgDotNetRuntime runtime, DbgDotNetValue obj) {
			var result = new List<HeapFieldInfo>();
			if (obj.Type.IsArray) {
				ReadArray(obj, result);
				return result;
			}
			foreach (var field in GetFields(obj.Type)) {
				evalInfo.CancellationToken.ThrowIfCancellationRequested();
				var res = runtime.LoadField(evalInfo, obj, field);
				if (res.HasError || res.ValueIsException || res.Value is null) {
					res.Value?.Dispose();
					result.Add(new HeapFieldInfo(field.Name, res.ErrorMessage ?? "?", FormatTypeName(field.FieldType), null));
					continue;
				}
				var value = res.Value;
				var text = FormatValue(value);
				bool isObject = !value.IsNull && !value.Type.IsValueType && !value.Type.IsPointer && value.Type != value.Type.AppDomain.System_String;
				result.Add(new HeapFieldInfo(field.Name, text, FormatTypeName(value.IsNull ? field.FieldType : value.Type), isObject ? value : null));
				if (!isObject)
					value.Dispose();
			}
			return result;
		}

		const int MaxArrayElements = 100;

		static void ReadArray(DbgDotNetValue array, List<HeapFieldInfo> result) {
			if (!array.GetArrayCount(out uint count))
				return;
			for (uint i = 0; i < count && i < MaxArrayElements; i++) {
				var res = array.GetArrayElementAt(i);
				if (res.Value is not DbgDotNetValue value)
					continue;
				bool isObject = !value.IsNull && !value.Type.IsValueType && !value.Type.IsPointer && value.Type != value.Type.AppDomain.System_String;
				result.Add(new HeapFieldInfo("[" + i.ToString(CultureInfo.InvariantCulture) + "]", FormatValue(value), FormatTypeName(value.Type), isObject ? value : null));
				if (!isObject)
					value.Dispose();
			}
			if (count > MaxArrayElements)
				result.Add(new HeapFieldInfo("...", string.Format(CultureInfo.InvariantCulture, "{0} more", count - MaxArrayElements), string.Empty, null));
		}

		/// <summary>
		/// A short summary of an object: "Name = "C1", Id = 1, ..." (the first few primitive and string fields)
		/// </summary>
		public static string GetSummary(DbgEvaluationInfo evalInfo, IDbgDotNetRuntime runtime, DbgDotNetValue obj) {
			if (obj.Type == obj.Type.AppDomain.System_String || obj.Type.IsArray)
				return FormatValue(obj);
			var sb = new StringBuilder();
			int count = 0;
			foreach (var field in GetFields(obj.Type)) {
				var ft = field.FieldType;
				if (!(ft.IsPrimitive || ft.IsEnum || ft == ft.AppDomain.System_String || ft == ft.AppDomain.System_Decimal))
					continue;
				var res = runtime.LoadField(evalInfo, obj, field);
				var value = res.Value;
				if (!res.HasError && !res.ValueIsException && value is not null) {
					if (count > 0)
						sb.Append(", ");
					sb.Append(field.Name).Append(" = ").Append(FormatValue(value));
					count++;
				}
				value?.Dispose();
				if (count >= 4)
					break;
			}
			return sb.ToString();
		}

		public static string FormatValue(DbgDotNetValue value) {
			if (value.IsNull)
				return "null";
			var type = value.Type;
			if (type.IsArray && value.GetArrayCount(out uint count))
				return FormatTypeName(type) + " Length = " + count.ToString(CultureInfo.InvariantCulture);
			var raw = value.GetRawValue();
			if (raw.HasRawValue) {
				switch (raw.RawValue) {
				case null:
					return "null";
				case string s:
					return "\"" + (s.Length > MaxStringLength ? s.Substring(0, MaxStringLength) + "..." : s) + "\"";
				case char c:
					return "'" + c.ToString() + "'";
				case bool b:
					return b ? "true" : "false";
				case IFormattable f:
					return f.ToString(null, CultureInfo.InvariantCulture);
				default:
					return raw.RawValue.ToString() ?? string.Empty;
				}
			}
			ulong address = GetAddress(value);
			return "{" + FormatTypeName(type) + "}" + (address != 0 && !type.IsValueType ? " " + FormatAddress(address) : string.Empty);
		}
	}
}
