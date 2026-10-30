/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// The methods marked PATCHED are the user's edits; V2 is a newer version of the app with other members (other tokens)
using System;
using System.Collections.Generic;
using System.Linq;

namespace PatchCases {
	struct Point {
		public int X, Y;
		public Point(int x, int y) { X = x; Y = y; }
		public override string ToString() => $"({X}, {Y})";
	}

	class Shop {
#if V2
		public string Currency = "EUR";
		public int Discount(int price) => price / 10;
#endif
		readonly List<int> prices = new List<int>();
		static int created;

		public Shop() => created++;

		public void Add(int price) => prices.Add(price);

		// Calls into the type itself and generic BCL methods, a field of a generic type
		public int Total() {
#if PATCHED
			int total = 0;
			foreach (var p in prices)
				total += Tax(p);
			return total + prices.Count;
#else
			return prices.Sum();
#endif
		}

		int Tax(int price) => price + price / 5;

		// switch, strings with a tab and a \0, try/catch with a catch type
		public static string Describe(int kind) {
#if PATCHED
			string s;
			switch (kind) {
			case 0: s = "zero\tnothing"; break;
			case 1: s = "one"; break;
			case 2: s = "two\0x"; break;
			default: s = "many"; break;
			}
			try {
				if (kind < 0)
					throw new ArgumentOutOfRangeException(nameof(kind));
			}
			catch (ArgumentException ex) {
				s = "bad " + ex.ParamName;
			}
			return s.Replace("\0", "\\0") + " #" + created;
#else
			return kind.ToString();
#endif
		}

		// Value types, arrays, a generic method of the type, typeof, double/long/float constants
		public static string Shapes() {
#if PATCHED
			var points = new Point[2];
			points[0] = new Point(1, 2);
			points[1] = new Point(3, 4);
			var grid = new int[2, 3];
			grid[1, 2] = 7;
			long big = 1L << 40;
			double d = 0.1 + 0.2;
			float f = 1.5f;
			var dict = new Dictionary<string, Point> { ["a"] = points[1] };
			var texts = new List<string>();
			foreach (var p in points)
				texts.Add(First(p).ToString());
			return string.Join(",", texts) + " " + grid[1, 2] + " " + big + " " + d.ToString(System.Globalization.CultureInfo.InvariantCulture) +
				" " + f.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + dict["a"] + " " + typeof(Shop).Name + " " + typeof(List<Point>).Name;
#else
			return "shapes";
#endif
		}

		static T First<T>(T value) => value;

		// fixed, pointers
		public static unsafe int Pointers() {
#if PATCHED
			int[] data = { 1, 2, 3 };
			int sum = 0;
			fixed (int* p = data) {
				for (int i = 0; i < data.Length; i++)
					sum += p[i];
			}
			return sum * 10;
#else
			return 0;
#endif
		}
	}

	static class Program {
		static void Main() {
			var shop = new Shop();
			shop.Add(10);
			shop.Add(25);
			Console.WriteLine("Total: " + shop.Total());
			for (int i = -1; i <= 3; i++)
				Console.WriteLine("Describe: " + Shop.Describe(i));
			Console.WriteLine("Shapes: " + Shop.Shapes());
			Console.WriteLine("Pointers: " + Shop.Pointers());
#if V2
			Console.WriteLine("Discount: " + shop.Discount(50) + " " + shop.Currency);
#endif
		}
	}
}
