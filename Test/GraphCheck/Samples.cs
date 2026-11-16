/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

using System;
using System.Collections.Generic;

// Sample code for the graphs; Program.cs loads this assembly with dnlib
namespace GraphSamples {
	interface IShape {
		double Area();
	}

	abstract class Shape : IShape {
		public abstract double Area();
		public string Describe() => "Area " + Area().ToString();
	}

	class Circle : Shape {
		readonly double r;
		public Circle(double r) => this.r = r;
		public override double Area() => Math.PI * r * r;
	}

	class Square : Shape {
		readonly double side;
		public Square(double side) => this.side = side;
		public override double Area() => side * side;
	}

	sealed class BigCircle : Circle {
		public BigCircle() : base(100) { }
	}

	static class Runner {
		public static double Total(IShape[] shapes) {
			double total = 0;
			foreach (var s in shapes)
				total += s.Area();
			return total;
		}

		public static void Run() {
			var c = new Circle(1);
			Console.WriteLine(c.Describe());
			Console.WriteLine(Total(new IShape[] { c, new Square(2) }));
			var list = new List<int>();
			list.Add(1);
		}
	}

	sealed class Caller {
		public void Go() => Runner.Run();
		public void GoTwice() {
			Go();
			Go();
		}
	}

	sealed class Recursion {
		public int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
		public int A(int n) => n == 0 ? 0 : B(n - 1);
		public int B(int n) => A(n);
	}
}
