using System;

namespace ExportCases {
	static class Program {
		static void Main() {
			// A span collection expression inside nested `fixed` statements that ICSharpCode.Decompiler duplicates
			// (a pinned string that can be null): its <>y__InlineArrayN local was declared at the start of the method
			Console.WriteLine(Spans.Sum("abc", "de"));
			Console.WriteLine(Spans.Sum("abc", null));
			Console.WriteLine(Spans.Sum5("a", "bc", null, "def", "g"));

			// A constructor whose parameters a lambda captures: the compiler copies them before calling base()
			var derived = new Derived("name", 3);
			Console.WriteLine(derived.Describe());
			Console.WriteLine(derived.BaseValue);

			// A file-local type declared in a file named PrivateImplementationDetails.cs, like Roslyn's
			Console.WriteLine(PrivateImplementationUser.Use(5));
		}
	}

	static unsafe class Spans {
		public static int Sum(string a, string? b) {
			fixed (char* pa = a) {
				fixed (char* pb = b) {
					// (Conditional elements are written through ref locals, which isn't handled yet: see docs/TODO.md)
					Span<int> values = [a.Length, pa[0], Length(b), First(pb)];
					return Total(values);
				}
			}
		}

		// Like Roslyn's CodeAnalysisEventSource: struct elements made from pinned strings, the span pinned again
		public static int Sum5(string a, string b, string? c, string d, string? e) {
			fixed (char* pa = a)
			fixed (char* pc = c)
			fixed (char* pe = e) {
				Span<Data> values = [Create(a, pa), Create(b, null), Create(c, pc), Create(d, null), Create(e, pe)];
				fixed (Data* p = values)
					return Total(p, values.Length);
			}
		}

		public struct Data {
			public int Length;
			public int First;
		}

		static Data Create(string? s, char* p) => new Data { Length = s?.Length ?? -1, First = p == null ? 0 : p[0] };

		static int Total(Data* data, int count) {
			int total = 0;
			for (int i = 0; i < count; i++)
				total = total * 31 + data[i].Length * 7 + data[i].First;
			return total;
		}

		static int Length(string? s) => s?.Length ?? -1;
		static int First(char* p) => p == null ? 0 : p[0];

		static int Total(Span<int> values) {
			int total = 0;
			foreach (var v in values)
				total = total * 31 + v;
			return total;
		}
	}

	class Base {
		public int BaseValue { get; }
		public Base() => BaseValue = 42;
	}

	sealed class Derived : Base {
		readonly string description;

		// The local function captures the parameters in a struct closure, which the compiler fills before base()
		public Derived(string name, int count) {
			description = describe();
			string describe() => count > 0 ? $"{name} x {count}" : name;
		}

		public string Describe() => description;
	}
}
