/*
    Copyright (C) 2014-2019 de4dot@gmail.com
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
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using dnlib.DotNet;
using dnlib.DotNet.Resources;
using dnSpy.Decompiler.Properties;

namespace dnSpy.Decompiler.MSBuild {
	/// <summary>
	/// Writes a .resources resource as a .resx file. The XML is created from the raw resource data
	/// (dnlib's <see cref="ResourceReader"/>) and nothing is deserialized: serialized objects are written
	/// as base64 with their mime type, streams and activator streams (eg. images in .NET 5+ apps) as
	/// separate files the .resx references (<c>ResXFileRef</c>).
	/// </summary>
	sealed class ResXProjectFile : ProjectFile {
		public override string Description => dnSpy_Decompiler_Resources.MSBuild_CreateResXFile;
		public override BuildAction BuildAction => BuildAction.EmbeddedResource;
		public override string Filename => filename;
		readonly string filename;

		public string TypeFullName { get; }
		public bool IsSatelliteFile { get; set; }

		readonly ModuleDef module;
		readonly EmbeddedResource embeddedResource;

		const string ResXReaderWriterAssembly = "System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
		const string BinaryMimeType = "application/x-microsoft.net.object.binary.base64";
		const string ByteArrayMimeType = "application/x-microsoft.net.object.bytearray.base64";
		const string FileRefTypeName = "System.Resources.ResXFileRef, " + ResXReaderWriterAssembly;
		const string FilesDirectoryName = "Resources";

		public ResXProjectFile(ModuleDef module, string filename, string typeFullName, EmbeddedResource er) {
			this.filename = filename;
			TypeFullName = typeFullName;
			this.module = module;
			embeddedResource = er;
		}

		public override void Create(DecompileContext ctx) {
			ResourceElementSet set;
			try {
				set = ResourceReader.Read(module, embeddedResource.CreateReader());
			}
			catch (Exception ex) {
				ctx.Logger.Error($"Could not read resources from {embeddedResource.Name}, Message: {ex.Message}");
				return;
			}

			var settings = new XmlWriterSettings {
				Indent = true,
				IndentChars = "  ",
				Encoding = new UTF8Encoding(true),
			};
			using (var writer = XmlWriter.Create(Filename, settings)) {
				writer.WriteStartElement("root");
				WriteHeader(writer, "resmimetype", "text/microsoft-resx");
				WriteHeader(writer, "version", "2.0");
				WriteHeader(writer, "reader", "System.Resources.ResXResourceReader, " + ResXReaderWriterAssembly);
				WriteHeader(writer, "writer", "System.Resources.ResXResourceWriter, " + ResXReaderWriterAssembly);

				int errors = 0;
				foreach (var elem in set.ResourceElements) {
					ctx.CancellationToken.ThrowIfCancellationRequested();
					try {
						WriteData(writer, elem);
					}
					catch (Exception ex) {
						if (errors++ < 30)
							ctx.Logger.Error($"Could not add resource '{elem.Name}', Message: {ex.Message}");
					}
				}

				writer.WriteEndElement();
			}
		}

		static void WriteHeader(XmlWriter writer, string name, string value) {
			writer.WriteStartElement("resheader");
			writer.WriteAttributeString("name", name);
			writer.WriteElementString("value", value);
			writer.WriteEndElement();
		}

		void WriteData(XmlWriter writer, ResourceElement elem) {
			// Get the value first so a resource that fails doesn't leave a half written <data> element
			string? typeName = null, mimeType = null;
			string value;
			bool preserveSpace = false;
			switch (elem.ResourceData) {
			case BuiltInResourceData builtIn:
				switch (builtIn.Code) {
				case ResourceTypeCode.Null:
					typeName = "System.Resources.ResXNullRef, " + ResXReaderWriterAssembly;
					value = string.Empty;
					break;
				case ResourceTypeCode.String:
					value = (string)builtIn.Data;
					preserveSpace = true;
					break;
				case ResourceTypeCode.ByteArray:
					typeName = "System.Byte[], mscorlib";
					value = Convert.ToBase64String((byte[])builtIn.Data);
					break;
				case ResourceTypeCode.Stream:
					typeName = FileRefTypeName;
					value = WriteFile(elem.Name, (byte[])builtIn.Data) + ";System.IO.MemoryStream, mscorlib";
					break;
				default:
					typeName = GetPrimitiveTypeName(builtIn.Code) + ", mscorlib";
					value = ToInvariantString(builtIn.Data);
					break;
				}
				break;

			case BinaryResourceData binary:
				switch (binary.Format) {
				case SerializationFormat.BinaryFormatter:
					mimeType = BinaryMimeType;
					value = Convert.ToBase64String(binary.Data);
					break;
				case SerializationFormat.TypeConverterByteArray:
					typeName = binary.TypeName;
					mimeType = ByteArrayMimeType;
					value = Convert.ToBase64String(binary.Data);
					break;
				case SerializationFormat.TypeConverterString:
					typeName = binary.TypeName;
					// UTF-8, the length is before the data
					value = Encoding.UTF8.GetString(binary.Data);
					preserveSpace = true;
					break;
				case SerializationFormat.ActivatorStream:
					typeName = FileRefTypeName;
					value = WriteFile(elem.Name, binary.Data) + ";" + binary.TypeName;
					break;
				default:
					throw new InvalidOperationException($"Unknown serialization format: {binary.Format}");
				}
				break;

			default:
				throw new InvalidOperationException($"Unknown resource data: {elem.ResourceData?.GetType()}");
			}

			writer.WriteStartElement("data");
			writer.WriteAttributeString("name", elem.Name);
			if (typeName is not null)
				writer.WriteAttributeString("type", typeName);
			if (mimeType is not null)
				writer.WriteAttributeString("mimetype", mimeType);
			if (preserveSpace)
				writer.WriteAttributeString("xml", "space", null, "preserve");
			writer.WriteElementString("value", value);
			writer.WriteEndElement();
		}

		static string GetPrimitiveTypeName(ResourceTypeCode code) =>
			code switch {
				ResourceTypeCode.Boolean => "System.Boolean",
				ResourceTypeCode.Char => "System.Char",
				ResourceTypeCode.Byte => "System.Byte",
				ResourceTypeCode.SByte => "System.SByte",
				ResourceTypeCode.Int16 => "System.Int16",
				ResourceTypeCode.UInt16 => "System.UInt16",
				ResourceTypeCode.Int32 => "System.Int32",
				ResourceTypeCode.UInt32 => "System.UInt32",
				ResourceTypeCode.Int64 => "System.Int64",
				ResourceTypeCode.UInt64 => "System.UInt64",
				ResourceTypeCode.Single => "System.Single",
				ResourceTypeCode.Double => "System.Double",
				ResourceTypeCode.Decimal => "System.Decimal",
				ResourceTypeCode.DateTime => "System.DateTime",
				ResourceTypeCode.TimeSpan => "System.TimeSpan",
				_ => throw new InvalidOperationException($"Unknown resource type: {code}"),
			};

		// The format ResXResourceReader's TypeConverters read (ConvertFromInvariantString)
		static string ToInvariantString(object data) =>
			data switch {
				float f => f.ToString("R", CultureInfo.InvariantCulture),
				double d => d.ToString("R", CultureInfo.InvariantCulture),
				DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
				TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
				IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
				_ => data.ToString() ?? string.Empty,
			};


		/// <summary>
		/// Writes the data to a new file in the Resources folder next to the .resx file and returns its
		/// path relative to the .resx file
		/// </summary>
		string WriteFile(string resourceName, byte[] data) {
			var dir = Path.Combine(Path.GetDirectoryName(Filename)!, FilesDirectoryName);
			Directory.CreateDirectory(dir);
			var name = FilenameUtils.CleanName(resourceName);
			if (string.IsNullOrEmpty(name))
				name = "resource";
			var ext = GetExtension(data);
			for (int i = 0; ; i++) {
				var fileName = i == 0 ? name + ext : name + "_" + i.ToString(CultureInfo.InvariantCulture) + ext;
				FileStream stream;
				try {
					// CreateNew: the .resx files are created in parallel and can have resources with the same name
					stream = new FileStream(Path.Combine(dir, fileName), FileMode.CreateNew, FileAccess.Write);
				}
				catch (IOException) when (i < 1000) {
					continue;
				}
				using (stream)
					stream.Write(data, 0, data.Length);
				return FilesDirectoryName + "\\" + fileName;
			}
		}

		static string GetExtension(byte[] data) {
			if (StartsWith(data, 0x89, 0x50, 0x4E, 0x47))
				return ".png";
			if (StartsWith(data, 0xFF, 0xD8, 0xFF))
				return ".jpg";
			if (StartsWith(data, 0x47, 0x49, 0x46, 0x38))
				return ".gif";
			if (StartsWith(data, 0x42, 0x4D))
				return ".bmp";
			if (StartsWith(data, 0x00, 0x00, 0x01, 0x00))
				return ".ico";
			if (StartsWith(data, 0x00, 0x00, 0x02, 0x00))
				return ".cur";
			if (StartsWith(data, 0x52, 0x49, 0x46, 0x46) && data.Length >= 12 && data[8] == 0x57 && data[9] == 0x41 && data[10] == 0x56 && data[11] == 0x45)
				return ".wav";
			return ".bin";
		}

		static bool StartsWith(byte[] data, params byte[] magic) {
			if (data.Length < magic.Length)
				return false;
			for (int i = 0; i < magic.Length; i++) {
				if (data[i] != magic[i])
					return false;
			}
			return true;
		}
	}
}
