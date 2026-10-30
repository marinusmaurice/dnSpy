using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffCases {
	// Same in both versions: no differences, also not in its lambda and iterator
	public class Unchanged {
		public int Value;
		public int Twice() => Value * 2;
		public IEnumerable<int> Evens(IEnumerable<int> items) => items.Where(a => a % 2 == 0);
		public IEnumerable<int> Count(int n) {
			for (int i = 0; i < n; i++)
				yield return i;
		}
	}

	public class Calculator {
		int lastResult;

		public int Add(int a, int b) {
#if V2
			lastResult = checked(a + b);
#else
			lastResult = a + b;
#endif
			return lastResult;
		}

		public int Subtract(int a, int b) => a - b;

#if V2
		public int Multiply(int a, int b) => a * b;
#else
		public int Divide(int a, int b) => a / b;
#endif

		// Changed lambda body (compiler-generated <>c class)
		public int SumOfSquares(IEnumerable<int> items) =>
#if V2
			items.Sum(a => a * a + 1);
#else
			items.Sum(a => a * a);
#endif
	}

#if V2
	public sealed class Logger : IDisposable {
		public void Dispose() { }
	}
#else
	public class OldHelper {
		public static string Name => "old";
	}
#endif

	public class Settings {
#if V2
		public const int Timeout = 60;
		public string Name { get; set; }
#else
		public const int Timeout = 30;
#endif
	}
}
