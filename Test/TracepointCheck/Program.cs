/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All tracepoint checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using System.Linq;
using dnSpy.Debugger.Breakpoints.Code.CondChecker;
int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
var parser = new TracepointMessageParser();
string Show(ParsedTracepointMessage m) => string.Join("|", m.Parts.Select(p => p.Kind == TracepointMessageKind.WriteText || p.Kind == TracepointMessageKind.WriteEvaluatedExpression ? $"{p.Kind}:{p.String}" : $"{p.Kind}#{p.Number}"));
void Expect(string text, string expected, int maxFrames, bool evaluates) {
	var m = parser.Parse(text);
	var s = Show(m);
	Check(s == expected, $"'{text}': got {s}, expected {expected}");
	Check(m.MaxFrames == maxFrames, $"'{text}': MaxFrames {m.MaxFrames}, expected {maxFrames}");
	Check(m.Evaluates == evaluates, $"'{text}': Evaluates {m.Evaluates}");
	Check(m.Parts.Sum(p => p.Length) == text.Length, $"'{text}': part lengths {m.Parts.Sum(p => p.Length)} != {text.Length}");
}
// New keywords: they don't need stack frames
Expect("hit $HITCOUNT", "WriteText:hit |WriteHitCount#0", 0, false);
Expect("$TICK ms", "WriteTickCount#0|WriteText: ms", 0, false);
Expect("[$TIME] x={x}", "WriteText:[|WriteTime#0|WriteText:] x=|WriteEvaluatedExpression:x", 0, true);
// $TID, $TIME, $TICK and $TNAME don't hide each other
Expect("$TID$TIME$TICK$TNAME", "WriteThreadId#0|WriteTime#0|WriteTickCount#0|WriteThreadName#0", 0, false);
Expect("$HITCOUNTS", "WriteHitCount#0|WriteText:S", 0, false);
// Older keywords and escapes still work
Expect("$FUNCTION: $CALLER2", "WriteFunction#0|WriteText:: |WriteCaller#2", 3, false);
Expect("$CALLSTACK5", "WriteCallStack#5", 5, false);
Expect(@"\$TIME \{a\} $hitcount", "WriteText:$TIME {a} $hitcount", 0, false);
Expect("$PID $PNAME $MID $ADID $BPADDR", "WriteProcessId#0|WriteText: |WriteProcessName#0|WriteText: |WriteManagedId#0|WriteText: |WriteAppDomainId#0|WriteText: |WriteBreakpointAddress#0", 0, false);
Expect("{a + {b}}", "WriteEvaluatedExpression:a + {b}", 0, true);
Expect("$", "WriteText:$", 0, false);
// Every keyword in the help text (ShowCodeBreakpointSettingsVM) is known
var help = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "ShowCodeBreakpointSettingsVM.cs"));
int start = help.IndexOf("$ADDRESS $ADDRESS1", StringComparison.Ordinal);
int end = help.IndexOf("\");", start, StringComparison.Ordinal);
var words = help.Substring(start, end - start).Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
Check(words.Length > 40 && words.Contains("$HITCOUNT") && words.Contains("$TICK") && words.Contains("$TIME"), "help lists the new keywords");
foreach (var w in words) {
	var m = parser.Parse(w);
	Check(m.Parts.Length == 1 && m.Parts[0].Kind != TracepointMessageKind.WriteText, $"help keyword {w} isn't parsed as one keyword");
}
// Dependent breakpoints: armed once a breakpoint with the label was hit, case and spaces ignored, cleared per session
var deps = new BreakpointDependencyTracker();
Check(deps.IsArmed(null) && deps.IsArmed("") && deps.IsArmed("  "), "no dependency is always armed");
Check(!deps.IsArmed("login"), "not armed before the label was hit");
deps.OnHit(new[] { "other", " " });
Check(!deps.IsArmed("login"), "another label doesn't arm it");
deps.OnHit(null);
deps.OnHit(new[] { " Login " });
Check(deps.IsArmed("login") && deps.IsArmed(" LOGIN"), "armed after a breakpoint with the label was hit");
Check(deps.IsArmed("other"), "every label of the hit breakpoint counts");
deps.Clear();
Check(!deps.IsArmed("login"), "a new debug session clears it");
var threads = System.Linq.Enumerable.Range(0, 8).Select(i => System.Threading.Tasks.Task.Run(() => { for (int j = 0; j < 1000; j++) { deps.OnHit(new[] { "t" + (j % 50) }); deps.IsArmed("t" + i); } })).ToArray();
System.Threading.Tasks.Task.WaitAll(threads);
Check(deps.IsArmed("t49"), "thread-safe");
if (fails == 0)
	Console.WriteLine("All tracepoint checks passed");
return fails == 0 ? 0 : 1;
