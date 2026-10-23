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
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace dnSpy.Debugger.DotNet.CorDebug.DAC {
	abstract class ClrDacProvider {
		public abstract ClrDac Create(int pid, string clrPath, IClrDacDebugger clrDacDebugger);
	}

	[Export(typeof(ClrDacProvider))]
	sealed class ClrDacProviderImpl : ClrDacProvider {
		public override ClrDac Create(int pid, string clrPath, IClrDacDebugger clrDacDebugger) {
			if (clrPath is null)
				throw new ArgumentNullException(nameof(clrPath));
			var clrDac = CreateCore(pid, clrPath, clrDacDebugger);
			Debug2.Assert(clrDac is not null);
			return clrDac ?? NullClrDac.Instance;
		}

		ClrDac? CreateCore(int pid, string clrPath, IClrDacDebugger clrDacDebugger) {
			DataTarget? dataTarget = null;
			bool failed = true;
			try {
				// Don't suspend: we're the debugger and only use it when the process is paused
				dataTarget = DataTarget.AttachToProcess(pid, suspend: false);
				var clrInfo = GetClrInfo(dataTarget, clrPath, out var localDac);
				if (clrInfo is null)
					return null;

				// Pass in the local dac file so it doesn't try to download it which
				// will block this thread for several seconds or much longer.
				var clrRuntime = clrInfo.CreateRuntime(localDac);

				var clrDac = new ClrDacImpl(dataTarget, clrRuntime, clrDacDebugger);
				failed = false;
				return clrDac;
			}
			catch (ClrDiagnosticsException) {
				return null;
			}
			catch (IOException) {
				return null;
			}
			catch (InvalidOperationException) {
				return null;
			}
			finally {
				if (failed)
					dataTarget?.Dispose();
			}
		}

		static ClrInfo? GetClrInfo(DataTarget dt, string clrPath, [NotNullWhen(true)] out string? localDac) {
			foreach (var clrInfo in dt.ClrVersions) {
				if (!StringComparer.OrdinalIgnoreCase.Equals(clrInfo.ModuleInfo.FileName, clrPath))
					continue;
				localDac = GetLocalDac(clrInfo);
				Debug2.Assert(localDac is not null);
				if (localDac is null)
					continue;
				return clrInfo;
			}
			localDac = null;
			return null;
		}

		// The dac file that's next to the CLR dll, eg. mscordaccore.dll
		static string? GetLocalDac(ClrInfo clrInfo) {
			foreach (var lib in clrInfo.DebuggingLibraries) {
				if (lib.Kind != DebugLibraryKind.Dac || lib.TargetArchitecture != RuntimeInformation.ProcessArchitecture)
					continue;
				if (Path.IsPathRooted(lib.FileName) && File.Exists(lib.FileName))
					return lib.FileName;
			}
			return null;
		}
	}
}
