namespace ExportCases {
	// The compiler names this type <PrivateImplementationDetails>F{SHA-256}__Helper, which ICSharpCode.Decompiler
	// hid as if it were the compiler's <PrivateImplementationDetails> class
	file sealed class Helper {
		readonly int factor;
		public Helper(int factor) => this.factor = factor;
		public int Apply(int value) => value * factor + 1;
	}

	static class PrivateImplementationUser {
		public static int Use(int value) => new Helper(3).Apply(value);
	}
}
