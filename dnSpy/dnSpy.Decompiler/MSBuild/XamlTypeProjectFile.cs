/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.MSBuild {
	sealed class XamlTypeProjectFile : TypeProjectFile {
		public XamlTypeProjectFile(TypeDef type, string filename, DecompilationContext decompilationContext, IDecompiler decompiler, Func<TextWriter, IDecompilerOutput> createDecompilerOutput)
			: base(type, filename, decompilationContext, decompiler, createDecompilerOutput) {
		}

		protected override void Decompile(DecompileContext ctx, IDecompilerOutput output) {
			var opts = new DecompilePartialType(output, decompilationContext, Type);
			foreach (var d in GetDefsToRemove())
				opts.Definitions.Add(d);
			// The XAML compiler adds them to the generated code (.g.cs) again. IStyleConnector is used when templates or
			// styles have events.
			foreach (var name in new[] { "IComponentConnector", "IStyleConnector" }) {
				opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", name, new AssemblyNameInfo("WindowsBase, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35").ToAssemblyRef()));
				opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", name, new AssemblyNameInfo("System.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089").ToAssemblyRef()));
			}
			decompiler.Decompile(DecompilationType.PartialType, opts);
		}

		IEnumerable<IMemberDef> GetDefsToRemove() {
			var ep = Type.Module.EntryPoint;
			if (ep is not null && ep.DeclaringType == Type)
				yield return ep;

			var d = FindInitializeComponent();
			if (d is not null) {
				yield return d;
				foreach (var f in DotNetUtils.GetFields(d)) {
					if (f.FieldType.RemovePinnedAndModifiers().GetElementType() == ElementType.Boolean)
						yield return f;
				}
			}

			foreach (var (connMeth, isComponentConnector) in FindConnectMethods()) {
				yield return connMeth;
				// IComponentConnector.Connect stores the x:Name fields, which are generated again too
				if (isComponentConnector) {
					foreach (var def in DotNetUtils.GetDefs(connMeth)) {
						if (def is FieldDef)
							yield return def;
						// VB: x:Names are properties (the field is accessed through them), Connect calls their setters
						else if (def is MethodDef setter && setter.IsSetter && FindProperty(setter) is PropertyDef prop) {
							foreach (var m in DotNetUtils.GetMethodsAndSelf(prop))
								yield return m;
							foreach (var f in DotNetUtils.GetFields(setter))
								yield return f;
						}
					}
				}
			}

			var delMeth = FindCreateDelegateMethod();
			if (delMeth is not null)
				yield return delMeth;
		}

		PropertyDef? FindProperty(MethodDef setter) {
			foreach (var p in Type.Properties) {
				if (p.SetMethods.Contains(setter))
					return p;
			}
			return null;
		}

		MethodDef? FindInitializeComponent() {
			foreach (var md in Type.FindMethods("InitializeComponent")) {
				if (md.IsStatic || md.Parameters.Count != 1)
					continue;
				if (md.ReturnType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void)
					continue;

				return md;
			}
			return null;
		}

		// IComponentConnector.Connect and IStyleConnector.Connect
		IEnumerable<(MethodDef method, bool isComponentConnector)> FindConnectMethods() {
			bool foundComponentConnector = false;
			foreach (var md in Type.Methods) {
				if (IsConnect(md, "System.Windows.Markup.IStyleConnector", false))
					yield return (md, false);
				else if (!foundComponentConnector && IsConnect(md, "System.Windows.Markup.IComponentConnector", true)) {
					foundComponentConnector = true;
					yield return (md, true);
				}
			}
		}

		static bool IsConnect(MethodDef md, string interfaceName, bool matchName) {
			if (md is null || md.IsStatic || md.Parameters.Count != 3)
				return false;
			if (md.ReturnType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void)
				return false;

			var sig = md.MethodSig;
			if (sig is null || sig.Params.Count != 2)
				return false;
			if (sig.Params[0].RemovePinnedAndModifiers().GetElementType() != ElementType.I4)
				return false;
			if (sig.Params[1].RemovePinnedAndModifiers().GetElementType() != ElementType.Object)
				return false;

			foreach (var o in md.Overrides) {
				if (o.MethodDeclaration is null || o.MethodDeclaration.DeclaringType is null)
					continue;
				if (o.MethodDeclaration.DeclaringType.FullName != interfaceName)
					continue;
				if (o.MethodDeclaration.Name != "Connect")
					continue;
				return true;
			}

			// A public Connect method (VB, older compilers) is IComponentConnector's
			return matchName && md.Overrides.Count == 0 && md.Name == "Connect";
		}

		// Finds 'internal Delegate _CreateDelegate(Type delegateType, string handler)' which is
		// called by XamlGeneratedNamespace.GeneratedInternalTypeHelper.CreateDelegate()
		MethodDef? FindCreateDelegateMethod() {
			foreach (var m in Type.Methods) {
				if (m.Name != "_CreateDelegate")
					continue;
				if (m.IsStatic || !m.IsAssembly)
					continue;
				var sig = m.MethodSig;
				if (sig.GetParamCount() != 2)
					continue;
				if (sig.RetType.RemovePinnedAndModifiers() is null || sig.RetType.RemovePinnedAndModifiers().ToString() != "System.Delegate")
					continue;
				if (sig.Params[0].RemovePinnedAndModifiers() is null || sig.Params[0].RemovePinnedAndModifiers().ToString() != "System.Type")
					continue;
				if (sig.Params[1].RemovePinnedAndModifiers() is null || sig.Params[1].RemovePinnedAndModifiers().GetElementType() != ElementType.String)
					continue;

				return m;
			}
			return null;
		}
	}
}
