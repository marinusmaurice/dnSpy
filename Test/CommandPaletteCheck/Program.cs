/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All command palette checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using dnSpy.MainApp.CommandPalette;
int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
var texts = new[] {
	"File > Open...",                       // 0
	"File > Close All",                     // 1
	"View > Full Screen",                   // 2
	"View > Collapse Tree View Nodes",      // 3
	"Debug > Step Over",                    // 4
	"Debug > Windows > Debug History",      // 5
	"Edit > Find",                          // 6
	"Window > Close All Documents",         // 7
	"Debug > Start Debugging...",           // 8
};
string First(string q) { var r = CommandPaletteMatcher.Filter(texts, q); return r.Count == 0 ? "" : texts[r[0]]; }
Check(CommandPaletteMatcher.Filter(texts, "").Count == texts.Length && CommandPaletteMatcher.Filter(texts, "  ")[0] == 0, "empty query: everything, menu order");
Check(First("full screen") == "View > Full Screen", "words: " + First("full screen"));
Check(First("fs") == "View > Full Screen", "initials: " + First("fs"));
Check(First("FULL") == "View > Full Screen", "case");
Check(First("debug history") == "Debug > Windows > Debug History", "path words");
Check(First("history") == "Debug > Windows > Debug History", "substring");
Check(CommandPaletteMatcher.Filter(texts, "close all").Count == 2, "two matches");
Check(First("close all") == "File > Close All", "shorter text first: " + First("close all"));
Check(CommandPaletteMatcher.Filter(texts, "zzz").Count == 0, "no match");
Check(CommandPaletteMatcher.Filter(texts, "step x").Count == 0, "every word must match");
Check(First("step") == "Debug > Step Over", "word start beats inner substring");
Check(First("ctvn") == "View > Collapse Tree View Nodes", "subsequence of word starts: " + First("ctvn"));
Check(First("debug start") == "Debug > Start Debugging...", "word order doesn't matter: " + First("debug start"));
// Word starts: camel case and after punctuation
Check(CommandPaletteMatcher.GetScore(CommandPaletteMatcher.GetWords("gb"), "GoToBrace") > CommandPaletteMatcher.GetScore(CommandPaletteMatcher.GetWords("gb"), "gabcd"), "camel case word starts");
Check(CommandPaletteMatcher.GetScore(Array.Empty<string>(), "x") == 0 && CommandPaletteMatcher.GetScore(new[] { "q" }, "x") == -1, "score edges");
Check(CommandPaletteMatcher.GetWords(null).Length == 0 && CommandPaletteMatcher.GetWords(" A\tb ").Length == 2 && CommandPaletteMatcher.GetWords("A")[0] == "a", "words");
// Access keys
Check(CommandPaletteMatcher.RemoveAccessKeys("_File") == "File" && CommandPaletteMatcher.RemoveAccessKeys("Save _All...") == "Save All...", "access keys");
Check(CommandPaletteMatcher.RemoveAccessKeys("A__B") == "A_B" && CommandPaletteMatcher.RemoveAccessKeys(null) == "" && CommandPaletteMatcher.RemoveAccessKeys("_") == "" && CommandPaletteMatcher.RemoveAccessKeys("1_0 x") == "10 x", "escaped underscores");
// Many items stay fast enough to filter on each key press
var many = new string[5000];
for (int i = 0; i < many.Length; i++) many[i] = $"Menu {i % 13} > Some Command Number {i} With A Longer Name";
var sw = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 10; i++) CommandPaletteMatcher.Filter(many, "cmd nmbr 42");
Check(sw.ElapsedMilliseconds < 2000, "speed: " + sw.ElapsedMilliseconds + " ms");
var exact = CommandPaletteMatcher.Filter(many, "number 4242");
Check(exact.Count > 0 && exact[0] == 4242, "substring before subsequence");
// Types and members: lower case texts passed in, at most maxResults results
var symbols = new[] { "System.Collections.Generic.List`1", "System.Collections.Generic.List`1.Add(T)", "System.Collections.Generic.List`1.AddRange(IEnumerable`1)", "System.Linq.Enumerable.Any(IEnumerable`1)" };
var lowerSymbols = Array.ConvertAll(symbols, s => s.ToLowerInvariant());
var la = CommandPaletteMatcher.Filter(symbols, lowerSymbols, "list add", 10);
Check(la.Count == 2 && la[0] == 1 && la[1] == 2, "symbols: list add");
Check(CommandPaletteMatcher.Filter(symbols, lowerSymbols, "", 2).Count == 2 && CommandPaletteMatcher.Filter(symbols, lowerSymbols, "list", 0).Count == 0, "symbols: max results");
Check(CommandPaletteMatcher.Filter(symbols, null, "enumerable any", 10)[0] == 3, "symbols: no lower texts");
var bigLower = Array.ConvertAll(many, s => s.ToLowerInvariant());
Check(CommandPaletteMatcher.Filter(many, bigLower, "command 4242", 200).Count <= 200, "symbols: limit with many");
// Type and member texts from a real module (this program): nested types, overloads, accessors left out
var mod = dnlib.DotNet.ModuleDefMD.Load(typeof(PaletteSample.Outer).Module);
var symTexts = new System.Collections.Generic.List<string>();
int collected = CommandPaletteSymbols.Collect(new[] { mod }, int.MaxValue, (text, m, def) => symTexts.Add(text));
Check(collected == symTexts.Count && symTexts.Contains("PaletteSample.Outer") && symTexts.Contains("PaletteSample.Outer.Inner"), "symbols: types and nested types");
Check(symTexts.Contains("PaletteSample.Outer.Run()") && symTexts.Contains("PaletteSample.Outer.Run(Int32, String)") && symTexts.Contains("PaletteSample.Outer.Inner.Go(List`1)"), "symbols: method overloads");
Check(symTexts.Contains("PaletteSample.Outer.Value") && symTexts.Contains("PaletteSample.Outer.Changed") && symTexts.Contains("PaletteSample.Outer.count"), "symbols: property, event, field");
Check(symTexts.Contains("PaletteSample.Outer.Gen`1.Put(T)"), "symbols: generic parameter names");
Check(!symTexts.Exists(s => s.Contains("get_Value") || s.Contains("add_Changed")), "symbols: no accessors");
int limited = 0;
CommandPaletteSymbols.Collect(new[] { mod }, 1, (text, m, def) => limited++);
Check(limited >= 1 && limited < symTexts.Count, "symbols: max");
var symLower = symTexts.ConvertAll(s => s.ToLowerInvariant());
var found = CommandPaletteMatcher.Filter(symTexts, symLower, "outer run int", 10);
Check(found.Count > 0 && symTexts[found[0]] == "PaletteSample.Outer.Run(Int32, String)", "symbols: search");
Console.WriteLine(fails == 0 ? "All command palette checks passed" : $"{fails} command palette checks failed");
return fails == 0 ? 0 : 1;

namespace PaletteSample {
	class Outer {
		int count;
		public int Value { get => count; set => count = value; }
		public event EventHandler? Changed;
		public void Run() => Changed?.Invoke(this, EventArgs.Empty);
		public void Run(int a, string b) => count = a + b.Length;
		public class Gen<T> { public void Put(T x) { } }
		public class Inner {
			public void Go(System.Collections.Generic.List<int> list) { }
		}
	}
}