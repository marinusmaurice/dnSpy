/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All Undo History checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.AsmEditor.UndoRedo;

int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }

var docA = new Doc("A.dll");
var docB = new Doc("B.dll");
var provider = new DocProvider(docA, docB);
var ctor = typeof(UndoCommandService).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
var undo = (IUndoCommandService)ctor.Invoke(new object[] { new Lazy<IUndoableDocumentsProvider>[] { new Lazy<IUndoableDocumentsProvider>(() => provider) } });

// The "documents" are counters: each edit adds to them, so the state shows which edits are applied
var log = new List<string>();
undo.Add(new Edit("Rename", docA, 1, log));
undo.Add(new Edit("Edit Method Code", docA, 10, log));
undo.Add(new Edit("Decrypt Strings", docB, 100, log));
Check(Same(undo.UndoHistory.Select(a => a.Description), "Rename", "Edit Method Code", "Decrypt Strings"), "three edits in the undo history, oldest first");
Check(undo.UndoHistory[0].Documents == "A.dll" && undo.UndoHistory[2].Documents == "B.dll", "the files of each edit");
Check(undo.RedoHistory.Count == 0, "nothing to redo");
Check(docA.Value == 11 && docB.Value == 100, "edits applied");

// Go back to before the first edit, then forward to the second
undo.GoTo(0);
Check(docA.Value == 0 && docB.Value == 0, "GoTo(0) undoes everything");
Check(undo.UndoHistory.Count == 0 && Same(undo.RedoHistory.Select(a => a.Description), "Rename", "Edit Method Code", "Decrypt Strings"), "GoTo(0): all three can be redone, the next one first");
undo.GoTo(2);
Check(docA.Value == 11 && docB.Value == 0, "GoTo(2) redoes the first two");
Check(undo.UndoHistory.Count == 2 && Same(undo.RedoHistory.Select(a => a.Description), "Decrypt Strings"), "GoTo(2): one left to redo");
Check(Same(log, "do Rename", "do Edit Method Code", "do Decrypt Strings", "undo Decrypt Strings", "undo Edit Method Code", "undo Rename", "do Rename", "do Edit Method Code"), "undo and redo in the right order: " + string.Join(", ", log));

// Going to the current state does nothing; out of range throws
log.Clear();
undo.GoTo(2);
Check(log.Count == 0, "GoTo(current) does nothing");
bool threw = false;
try { undo.GoTo(4); } catch (ArgumentOutOfRangeException) { threw = true; }
Check(threw, "GoTo past the last edit throws");

// A new edit after undoing drops the undone ones, like Undo does
undo.Add(new Edit("Add Class", docB, 1000, log));
Check(Same(undo.UndoHistory.Select(a => a.Description), "Rename", "Edit Method Code", "Add Class") && undo.RedoHistory.Count == 0, "a new edit clears the redo history");

// GoTo undoes one edit at a time (the Undo History window refreshes once at the end)
var events = 0;
undo.OnEvent += (s, e) => { if (e.Type == UndoCommandServiceEventType.Undo) events++; };
undo.GoTo(0);
Check(events == 3, $"GoTo(0) from 3 edits: 3 undo events (got {events})");
undo.Clear();
Check(undo.UndoHistory.Count == 0 && undo.RedoHistory.Count == 0, "Clear empties the history");

// The edit's time is when it was made
var before = DateTime.Now;
undo.Add(new Edit("Rename", docA, 1, log));
Check(undo.UndoHistory[0].Time >= before.AddSeconds(-1) && undo.UndoHistory[0].Time <= DateTime.Now.AddSeconds(1), "the edit's time");

if (fails == 0) {
	Console.WriteLine("All Undo History checks passed (history, files, GoTo back and forward, redo cleared by a new edit)");
	return 0;
}
Console.WriteLine($"{fails} check(s) failed");
return 1;

static bool Same(IEnumerable<string> a, params string[] b) => a.SequenceEqual(b);

sealed class Doc {
	public string Name { get; }
	public int Value { get; set; }
	public UndoObject UndoObject { get; }
	public Doc(string name) { Name = name; UndoObject = new UndoObject(this); }
}

sealed class DocProvider : IUndoableDocumentsProvider {
	readonly Doc[] docs;
	public DocProvider(params Doc[] docs) => this.docs = docs;
	public IEnumerable<IUndoObject> GetObjects() => docs.Select(a => a.UndoObject);
	public IUndoObject? GetUndoObject(object obj) => (obj as Doc)?.UndoObject;
	public bool OnExecutedOneCommand(IUndoObject obj) => true;
	public object? GetDocument(IUndoObject obj) => (obj as UndoObject)?.Value as Doc;
}

sealed class Edit : IUndoCommand {
	readonly Doc doc;
	readonly int amount;
	readonly List<string> log;
	public string Description { get; }
	public IEnumerable<object> ModifiedObjects => new[] { doc };
	public Edit(string description, Doc doc, int amount, List<string> log) { Description = description; this.doc = doc; this.amount = amount; this.log = log; }
	public void Execute() { doc.Value += amount; log.Add("do " + Description); }
	public void Undo() { doc.Value -= amount; log.Add("undo " + Description); }
}

namespace dnSpy.AsmEditor.UndoRedo {
	// The real one names dnSpy's documents
	static class UndoHistoryNames {
		public static string GetDocumentName(object document) => (document as Doc)?.Name ?? document.ToString() ?? string.Empty;
	}
}
