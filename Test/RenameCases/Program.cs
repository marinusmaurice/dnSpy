/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Rename map test app (see ../RenameCheck): RenameCheck "obfuscates" v1 and v2 with other names, renames some types
// and members of obfuscated v1 back, makes a rename map and applies it to obfuscated v2. V2 is a newer version: more
// members, a changed method, a new type. The output doesn't depend on names, so every build prints the same.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RenameCases {
	[AttributeUsage(AttributeTargets.Class)]
	sealed class TagAttribute : Attribute {
		// Set by named arguments, which hold the names: renaming them must rename the arguments too
		public string Label { get; set; }
		public int Level;
	}

	[Tag(Label = "config", Level = 2)]
	sealed class Config {
		public readonly Dictionary<string, int> Values = new Dictionary<string, int>();
		public int Get(string name) => Values.TryGetValue(name, out var value) ? value : -1;
	}

	sealed class LicenseChecker {
		readonly string key;
		public int Attempts { get; private set; }
		public event EventHandler Checked;

		public LicenseChecker(string key) => this.key = key;

		public bool Validate(string user) {
			Attempts++;
			Checked?.Invoke(this, EventArgs.Empty);
			return Checksum(user) == Checksum(key) % 1000;
		}

		int Checksum(string s) {
			int sum = 17;
			foreach (var c in s)
				sum = sum * 31 + c;
#if V2
			// The new version changed this method
			sum ^= 0x5A5A;
#endif
			return Math.Abs(sum) % 1000;
		}

#if V2
		public void Reset() => Attempts = 0;
#endif
	}

	// Called through member references (Box<int>::Get), which hold the names too
	sealed class Box<T> {
		T value;
		public T Get() => value;
		public void Set(T newValue) => value = newValue;
	}

	// Two types with the same code: a rename of one can't be applied to a new version with other names (which is which?)
	sealed class Twin1 {
		public int Value() => 1;
	}

	sealed class Twin2 {
		public int Value() => 1;
	}

#if V2
	sealed class NewFeature {
		public string Describe() => "new";
	}
#endif

	static class Program {
		static int Main() {
			var checker = new LicenseChecker("secret");
			int events = 0;
			checker.Checked += (s, e) => events++;
			Console.WriteLine($"valid: {checker.Validate("someone")}, {checker.Validate("secret")}");
			Console.WriteLine($"attempts: {checker.Attempts}, events: {events}");

			var box = new Box<int>();
			box.Set(42);
			var names = new Box<string>();
			names.Set("box");
			Console.WriteLine($"box: {box.Get()} {names.Get()}");

			var tag = (TagAttribute)typeof(Config).GetCustomAttributes(typeof(TagAttribute), false).Single();
			Console.WriteLine($"tag: {tag.Label} {tag.Level}");

			var config = new Config();
			config.Values["a"] = 1;
			Console.WriteLine($"config: {config.Get("a")} {config.Get("b")}");
			Console.WriteLine($"twins: {new Twin1().Value() + new Twin2().Value()}");
#if V2
			Console.WriteLine(new NewFeature().Describe().Length > 0 ? "v2" : "?");
#else
			Console.WriteLine("v2");
#endif
			return 0;
		}
	}
}
