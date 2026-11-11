<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# String decryption

Version 7.32.0.0. The engine and `dnSpy.Console --decrypt-strings` are tested headlessly (`Test/StringDecryptCases`, `check.ps1`); the menu commands are click-tested (see [Click-test steps](#click-test-steps)).

## Goal

Obfuscators replace the strings of a program with calls like `a.b(1234)` or `<Module>.\u0001<string>(0x5EED122D)`. The decryptor method returns the real string at run time, so the decompiled code shows no text and is hard to read. **Decrypt Strings...** finds the decryptors, works out what each call returns and puts the string in its place: `Console.WriteLine(a.b(1234))` becomes `Console.WriteLine("Licence key accepted")`.

Tools like de4dot do this by running the decryptor, ie. by loading and running the obfuscated assembly's code. That code can do anything (obfuscators add anti-debugging and anti-tamper code, and malware is obfuscated too). dnSpy runs it in an **emulator** instead: none of the assembly's code runs. The emulator runs its IL instructions itself, and only calls .NET methods that compute something.

## Using it

- **File → Decrypt Strings...** (or right-click an assembly or module): lists the methods that look like decryptors, with how many calls each has, and asks before decrypting. The message afterwards lists the decrypted calls, the ones that couldn't be decrypted with the reason (eg. `System.IO.File.WriteAllText isn't allowed`), and the module initializers that were skipped. One edit for Edit → Undo / Redo. File → Save Module saves the decrypted assembly.
- **Decrypt Strings with This Method** (right-click a method in the assembly explorer or the code view): the same with only that method as the decryptor, for one the search doesn't pick (eg. with one call).
- `dnSpy.Console --decrypt-strings -o decrypted.dll obfuscated.dll`: prints `* <decryptor>: N call(s)` per decryptor, `+ <method> IL_xxxx: "string"` per decrypted call, `! <method> IL_xxxx (<decryptor>): reason` per call that wasn't (exit code 1 then; the output file is still written), and `? skipped (module initializer): ...`. `--decryptor 0x06000012` (more than once for more) uses only those methods.

## Which methods are decryptors

A static method that returns a string (or a generic `T Get<T>(...)` called as `Get<string>`, like ConfuserEx's), takes 1 to 8 numbers, chars, bools or strings, and is called with constants (`ldc.*`, `ldstr` right before the call). Every such call is a call site; calls with computed arguments are left alone. A call is only replaced when the decryptor returns a string (not null). Check the list before saying Yes: a method that isn't a decryptor but has that shape, eg. one that looks up a name in a table the program fills while it runs, would be replaced by what it returns at start-up (the emulator only runs static constructors).

The first argument instruction becomes `ldstr "..."`, the other argument instructions and the call become `nop`, so branches to the call stay valid. Calls that a branch jumps into the middle of are left alone.

## The emulator

`dnSpy/dnSpy.Decompiler/StringDecryption/ILEmulator.cs`, on dnlib's IL (not the debugger's interpreter: that one needs a process or a full set of loaded assemblies, and this works on the module in memory, edits included):

- All IL instructions a decryptor needs: arithmetic (with overflow checks), conversions, branches, `switch`, locals and arguments with their addresses, static and instance fields, arrays (real .NET arrays) and their element addresses, `box`/`unbox.any`, `isinst`/`castclass`, `ldtoken`, calls, `newobj`, `try`/`finally` (finally blocks run on `leave`). A `throw` stops the call (the call isn't decrypted). Not supported: `catch`, delegates, pointers, `calli`, `localloc`, structs of the assembly.
- The assembly's own classes are emulated objects with their fields; virtual calls go to the override. Static constructors run once, the first time a type is used, and their static fields are kept for all calls (eg. a key table or a cache filled on the first call). `RuntimeHelpers.InitializeArray` copies the field's initial data.
- `Assembly.GetExecutingAssembly()` / `GetCallingAssembly()`, `typeof(X).Assembly`, `GetManifestResourceStream` (from the assembly's embedded resources), `typeof(T) == typeof(string)`, `lock` and `Interlocked` are done by the emulator.
- .NET methods are called for real only on these types (`FrameworkCalls.cs`): `String`, `Char`, the number types, `Math`, `Convert`, `BitConverter`, `Buffer`, `Array`, `StringBuilder`, the encodings, `MemoryStream` and `Stream`, `BinaryReader`/`BinaryWriter`/`StreamReader`/`StreamWriter` over a stream (not a file name), `DeflateStream`/`GZipStream`, the symmetric ciphers (AES, Rijndael, DES, TripleDES, RC2), `CryptoStream`, hashes and HMACs, `Rfc2898DeriveBytes`/`PasswordDeriveBytes`, `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `Hashtable`, `ArrayList`, and of `Type` only comparing and names. Anything else (files, `Path`, `Environment`, `Process`, the registry, the network, reflection, `Debugger`, P/Invoke) stops the call with "isn't allowed".
- Limits: 20 million instructions per call, 200 million for a static constructor, 200 nested calls, arrays up to 64M elements. A run of the same decryptor with the same arguments is done once.
- `<Module>`'s static constructor, when it only calls methods (eg. ConfuserEx's, or C# `[ModuleInitializer]`s), runs each call on its own: one that fails (eg. anti-debugging code that calls `Debugger.IsAttached`) is skipped and listed, so the decryptor's own initializer still runs.

## Code

- `dnSpy/dnSpy.Decompiler/StringDecryption/StringDecryptor.cs`: `StringDecryptor.FindDecryptors(module, only)` → `StringDecryptorInfo` (method, call sites), `CanBeDecryptor(method)`, `Decrypt(module, decryptors)` → `StringDecryptionResult` (`Decrypted`, `Failed`, `SkippedInitializers`, `ChangedMethods`, `Undo()`/`Redo()`; the changes are made). Runs on its own thread with a 64 MB stack.
- `dnSpy/dnSpy.Decompiler/StringDecryption/ILEmulator.cs`, `FrameworkCalls.cs`: the emulator and the allowed .NET types.
- `Extensions/dnSpy.AsmEditor/StringDecryption/StringDecryptionCommands.cs`: the menu commands and `DecryptStringsUndoCommand`.
- `dnSpy.Console`: `--decrypt-strings`, `--decryptor`.
- No contract changes.

## Tests

`Test/StringDecryptCases` prints 11 strings; 10 come from 6 decryptors made like obfuscators do (the encrypted data is made by its `generate.ps1`): a byte table XORed with a key (arrays with initial data), base64 of reversed UTF-16 (a string argument), an embedded resource read with `BinaryReader` into a `Dictionary` cache under a `lock`, a generic `Get<T>(uint)` whose table is set up by a `[ModuleInitializer]` next to an anti-debugging one, AES-256 with a SHA-256 key, and the assembly's own classes with a virtual method and an override. The 11th, `BadStrings.Get`, writes a file. `check.ps1` runs `dnSpy.Console --decrypt-strings` and checks: the 10 strings in order, `BadStrings.Get` not decrypted (`Path.GetTempPath isn't allowed`) and its file not written, the anti-debugging initializer skipped, exit code 1, the decrypted program prints the same, and the decompiled `Main` has the strings.

## Click-test steps

1. Build `Test/StringDecryptCases`: `dotnet build Test/StringDecryptCases -c Release -o %TEMP%\sdc`.
2. Open `%TEMP%\sdc\StringDecryptCases.dll`, look at `Program.Main`: calls like `XorStrings.Get(0)`.
3. Right-click the assembly → **Decrypt Strings...**: 7 decryptors listed; Yes: 10 of 11 decrypted, `BadStrings.Get` not (`Path.GetTempPath isn't allowed`), `AntiDebug` skipped. `Main` now shows the strings.
4. Edit → **Undo**: the calls are back; **Redo**.
5. Undo, then right-click `XorStrings.Get` → **Decrypt Strings with This Method**: only its 2 calls.
6. File → Save Module, run the saved program: same output.
