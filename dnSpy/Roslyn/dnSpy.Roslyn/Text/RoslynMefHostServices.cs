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
using System.Composition.Hosting;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using dnSpy.Roslyn.Internal.SignatureHelp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;

namespace dnSpy.Roslyn.Text {
	/// <summary>
	/// Shared <see cref="MefHostServices"/> instance
	/// </summary>
	public sealed class RoslynMefHostServices {
		/// <summary>
		/// Gets the shared <see cref="MefHostServices"/> instance
		/// </summary>
		public static MefHostServices DefaultServices {
			get {
				if (defaultServices is null)
					Interlocked.CompareExchange(ref defaultServices, CreateDefaultServices(), null);
				Debug2.Assert(defaultServices is not null);
				return defaultServices;
			}
		}
		static MefHostServices? defaultServices;

		static MefHostServices CreateDefaultServices() {
			var asms = new HashSet<Assembly>(MefHostServices.DefaultAssemblies);
			var version = typeof(Compilation).Assembly.GetName().Version;
			foreach (var asmNameTmp in otherAssemblies) {
				var asmName = string.Format(asmNameTmp, version);
				try {
					asms.Add(Assembly.Load(asmName));
				}
				catch {
					Debug.Fail($"Couldn't load Roslyn MEF assembly: {asmName}");
				}
			}
			// dnSpy.Roslyn.Internal exports some stuff too
			asms.Add(typeof(SignatureHelpService).Assembly);
			var parts = new List<Type>();
			foreach (var asm in asms)
				parts.AddRange(GetTypes(asm).Where(a => !IsExcludedPart(a)));
			return MefHostServices.Create(new ContainerConfiguration().WithParts(parts).CreateContainer());
		}

		// Parts whose required imports are only exported by VS (eg. IntelliCode). System.Composition validates
		// every [ImportMany] dependency so they'd prevent eg. all signature help providers from being created.
		static bool IsExcludedPart(Type type) => type.Namespace?.Contains(".ExternalAccess.Pythia") == true;

		static Type[] GetTypes(Assembly asm) {
			try {
				return asm.GetTypes();
			}
			catch (ReflectionTypeLoadException ex) {
				return ex.Types.Where(a => a is not null).ToArray()!;
			}
		}
		static readonly string[] otherAssemblies = new string[] {
			"Microsoft.CodeAnalysis.Features, Version={0}, Culture=neutral, PublicKeyToken=31bf3856ad364e35",
			"Microsoft.CodeAnalysis.CSharp.Features, Version={0}, Culture=neutral, PublicKeyToken=31bf3856ad364e35",
			"Microsoft.CodeAnalysis.VisualBasic.Features, Version={0}, Culture=neutral, PublicKeyToken=31bf3856ad364e35",
		};
	}
}
