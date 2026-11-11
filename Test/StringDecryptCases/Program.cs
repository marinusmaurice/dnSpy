/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// String decryption test app (see ../DecompilerStress/check.ps1). Every string Main prints is encrypted the way an
// obfuscator does it (the data is in Data.cs and strings.bin, made by generate.ps1), so the program has no plain
// text. dnSpy.Console --decrypt-strings must replace every decryptor call by its string, except the call of Bad,
// which writes a file and must not run, and the decrypted program must print the same.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace StringDecryptCases {
	// A byte table XORed with a key, both arrays with initial data (RuntimeHelpers.InitializeArray in the static constructor)
	static partial class XorStrings {
		static readonly byte[] key = { 0x5A, 0x13, 0xC7, 0x2E, 0x91 };

		public static string Get(int id) {
			int offset = offsets[id];
			var bytes = new byte[lengths[id]];
			for (int i = 0; i < bytes.Length; i++)
				bytes[i] = (byte)(blob[offset + i] ^ key[(offset + i) % key.Length]);
			return Encoding.UTF8.GetString(bytes);
		}
	}

	// A string argument: base64 of reversed UTF-16
	static class Base64Strings {
		public static string Decode(string s) {
			var bytes = Convert.FromBase64String(s);
			Array.Reverse(bytes);
			return Encoding.Unicode.GetString(bytes);
		}
	}

	// Strings in an embedded resource, read once into a cache under a lock (try/finally)
	static class ResourceStrings {
		static readonly object sync = new object();
		static Dictionary<int, string> cache;

		public static string Read(int index) {
			lock (sync) {
				if (cache is null) {
					cache = new Dictionary<int, string>();
					using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("strings.bin"))
					using (var reader = new BinaryReader(stream)) {
						int count = reader.ReadInt32();
						for (int i = 0; i < count; i++) {
							var sb = new StringBuilder(reader.ReadString());
							for (int j = 0; j < sb.Length; j++)
								sb[j] = (char)(sb[j] ^ 0x2A);
							cache[i] = sb.ToString();
						}
					}
				}
				return cache.TryGetValue(index, out var value) ? value : null;
			}
		}
	}

	// Like ConfuserEx: a generic T Get<T>(uint id), data set up by a module initializer (in <Module>'s static constructor,
	// next to an anti-debugging initializer that can't run in the emulator)
	static partial class GenericStrings {
		static char[] pool;

		[ModuleInitializer]
		internal static void Initialize() {
			pool = new char[raw.Length];
			for (int i = 0; i < raw.Length; i++)
				pool[i] = (char)raw[i];
		}

		[ModuleInitializer]
		internal static void AntiDebug() {
			if (Debugger.IsAttached)
				Environment.FailFast("debugger");
		}

		public static T Get<T>(uint id) {
			if (Assembly.GetCallingAssembly() != Assembly.GetExecutingAssembly())
				return default;
			if (typeof(T) != typeof(string))
				return default;
			uint v = id ^ 0x5EED1234;
			int start = (int)(v >> 12);
			int length = (int)(v & 0xFFF);
			var chars = new char[length];
			for (int i = 0; i < length; i++)
				chars[i] = (char)(pool[start + i] ^ (ushort)(0x3C1 + (start + i) * 7));
			return (T)(object)new string(chars);
		}
	}

	// AES-256 (CBC), the key a SHA-256 hash of a password
	static partial class AesStrings {
		public static string Decrypt(int id) {
			using (var aes = Aes.Create())
			using (var sha = SHA256.Create()) {
				aes.Key = sha.ComputeHash(Encoding.UTF8.GetBytes("dnSpy test key"));
				aes.IV = new byte[16];
				using (var transform = aes.CreateDecryptor()) {
					var plain = transform.TransformFinalBlock(aesBlob, aesOffsets[id], aesLengths[id]);
					return Encoding.UTF8.GetString(plain);
				}
			}
		}
	}

	// Objects of the assembly's own classes, with a virtual method and an override
	class Rotor {
		readonly int shift;
		public Rotor(int shift) => this.shift = shift;

		public virtual char Shift(char c) {
			if (c >= 'a' && c <= 'z')
				return (char)('a' + (c - 'a' + 26 - shift) % 26);
			if (c >= 'A' && c <= 'Z')
				return (char)('A' + (c - 'A' + 26 - shift) % 26);
			return c;
		}

		public string Decode(string s) {
			var chars = s.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
				chars[i] = Shift(chars[i]);
			return new string(chars);
		}
	}

	sealed class DoubleRotor : Rotor {
		public DoubleRotor(int shift) : base(shift) { }
		public override char Shift(char c) => base.Shift(base.Shift(c));
	}

	static partial class RotorStrings {
		public static string Get(int id) {
			Rotor rotor = id % 2 == 0 ? new Rotor(3) : new DoubleRotor(5);
			return rotor.Decode(rotated[id]);
		}
	}

	// Writes a file: must not run when decrypting (File and Path aren't allowed), so this call stays
	static class BadStrings {
		public static string Get(int id) {
			File.WriteAllText(Path.Combine(Path.GetTempPath(), "dnspy-decrypt-marker.txt"), "ran");
			return "Bad call " + id.ToString();
		}
	}

	static class Program {
		static void Main() {
			Console.WriteLine(XorStrings.Get(0));
			Console.WriteLine(XorStrings.Get(1));
			Console.WriteLine(Base64Strings.Decode(Data.Base64Text));
			Console.WriteLine(ResourceStrings.Read(0));
			Console.WriteLine(ResourceStrings.Read(1));
			Console.WriteLine(GenericStrings.Get<string>(Data.GenericId0));
			Console.WriteLine(GenericStrings.Get<string>(Data.GenericId1));
			Console.WriteLine(AesStrings.Decrypt(0));
			Console.WriteLine(RotorStrings.Get(0));
			Console.WriteLine(RotorStrings.Get(1));
			Console.WriteLine(BadStrings.Get(7));
		}
	}
}
