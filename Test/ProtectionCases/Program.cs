/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

using System;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class Program {
	static int Main(string[] args) {
		if (args.Length != 2) {
			Console.Error.WriteLine("Usage: ProtectionCases <dnSpy DLL folder> <output file>");
			return 2;
		}
		var bin = Path.GetFullPath(args[0]);
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
			var file = Path.Combine(bin, new System.Reflection.AssemblyName(e.Name).Name + ".dll");
			return File.Exists(file) ? System.Reflection.Assembly.LoadFrom(file) : null;
		};
		Write(Path.GetFullPath(args[1]));
		return 0;
	}

	// Separate method so dnlib is loaded after AssemblyResolve is hooked. The assembly has:
	//   - ConfuserEx's marker: a ConfusedByAttribute type, used on the assembly
	//   - [assembly: SuppressIldasm]
	//   - 20 types with a field each whose names are zero-width/format characters (unreadable names)
	//   - a costura.*.dll.compressed resource (Costura.Fody's embedded assemblies)
	//   - Program.Main calls Assembly.Load(byte[]) (a hint)
	static void Write(string output) {
		var module = new ModuleDefUser("ProtectionCases.dll");
		module.Kind = ModuleKind.Dll;
		var assembly = new AssemblyDefUser("ProtectionCases", new Version(1, 0, 0, 0));
		assembly.Modules.Add(module);
		var corlib = module.CorLibTypes;

		var attributeType = new TypeRefUser(module, "System", "Attribute", corlib.AssemblyRef);
		var attributeCtor = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(corlib.Void), attributeType);
		var confusedBy = new TypeDefUser(string.Empty, "ConfusedByAttribute", attributeType);
		confusedBy.Attributes = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed;
		module.Types.Add(confusedBy);
		var confusedByCtor = AddCtor(confusedBy, attributeCtor);
		assembly.CustomAttributes.Add(new CustomAttribute(confusedByCtor));

		var suppressIldasm = new TypeRefUser(module, "System.Runtime.CompilerServices", "SuppressIldasmAttribute", corlib.AssemblyRef);
		assembly.CustomAttributes.Add(new CustomAttribute(new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(corlib.Void), suppressIldasm)));

		var objectCtor = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(corlib.Void), corlib.Object.TypeDefOrRef);
		for (int i = 0; i < 20; i++) {
			// Each name a different mix of zero-width space/non-joiner/joiner, like obfuscators do
			var name = string.Empty;
			for (int n = i + 1; n != 0; n >>= 1)
				name += (n & 1) != 0 ? ((char)0x200B).ToString() : ((char)0x200C).ToString();
			var type = new TypeDefUser("Obfuscated", name + ((char)0x200D).ToString(), corlib.Object.TypeDefOrRef);
			type.Attributes = TypeAttributes.NotPublic | TypeAttributes.Class;
			type.Fields.Add(new FieldDefUser(((char)0x2028).ToString() + name, new FieldSig(corlib.Int32), FieldAttributes.Assembly));
			module.Types.Add(type);
			AddCtor(type, objectCtor);
		}

		var program = new TypeDefUser("ProtectionCases", "Program", corlib.Object.TypeDefOrRef);
		program.Attributes = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed;
		module.Types.Add(program);
		var assemblyType = new TypeRefUser(module, "System.Reflection", "Assembly", corlib.AssemblyRef);
		var load = new MemberRefUser(module, "Load", MethodSig.CreateStatic(new ClassSig(assemblyType), new SZArraySig(corlib.Byte)), assemblyType);
		var main = new MethodDefUser("Main", MethodSig.CreateStatic(corlib.Void), MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
		main.Body = new CilBody();
		main.Body.Instructions.Add(OpCodes.Ldnull.ToInstruction());
		main.Body.Instructions.Add(OpCodes.Call.ToInstruction(load));
		main.Body.Instructions.Add(OpCodes.Pop.ToInstruction());
		main.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
		program.Methods.Add(main);

		module.Resources.Add(new EmbeddedResource("costura.helper.dll.compressed", new byte[] { 1, 2, 3, 4 }));
		module.Write(output);
	}

	static MethodDef AddCtor(TypeDef type, IMethod baseCtor) {
		var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(type.Module.CorLibTypes.Void),
			MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
		ctor.Body = new CilBody();
		ctor.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
		ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(baseCtor));
		ctor.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
		type.Methods.Add(ctor);
		return ctor;
	}
}
