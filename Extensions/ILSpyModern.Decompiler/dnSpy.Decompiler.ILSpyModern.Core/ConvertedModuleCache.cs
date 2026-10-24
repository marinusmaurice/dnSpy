/*
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
using System.Threading;
using dnlib.DotNet;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Keeps the last converted modules between decompilations. Writing a big module to memory
	/// takes much longer than decompiling one method (System.Private.CoreLib: about 0.5 s).
	/// Whoever modifies a module must call <see cref="OnModuleModified(ModuleDef)"/>.
	/// </summary>
	public static class ConvertedModuleCache {
		// Each entry holds a copy of the module's PE image. A module's references are
		// all of its assemblies' references (ICSharpCode.Decompiler's type system loads them all).
		const int MaxMainModules = 4;
		const int MaxReferencedModules = 128;
		const long MaxReferencedModulesBytes = 256 * 1024 * 1024;

		sealed class Entry {
			public readonly ModuleDef Module;
			public readonly bool InMemory;
			public readonly Lazy<ConvertedModule> Converted;
			public long ImageSize => Converted.IsValueCreated ? Converted.Value.ImageSize : 0;

			public Entry(ModuleDef module, bool inMemory) {
				Module = module;
				InMemory = inMemory;
				Converted = new Lazy<ConvertedModule>(() => inMemory ? ConvertedModule.FromModuleInMemory(module) : ConvertedModule.FromModuleOnDisk(module),
					LazyThreadSafetyMode.ExecutionAndPublication);
			}
		}

		static readonly object lockObj = new object();
		// Most recently used first
		static readonly LinkedList<Entry> mainEntries = new LinkedList<Entry>();
		static readonly LinkedList<Entry> referencedEntries = new LinkedList<Entry>();

		internal static ConvertedModule GetOrCreate(ModuleDef module, bool inMemory) {
			Entry? entry = null;
			var entries = inMemory ? mainEntries : referencedEntries;
			lock (lockObj) {
				for (var node = entries.First; node is not null; node = node.Next) {
					var e = node.Value;
					if (e.Module != module || e.InMemory != inMemory)
						continue;
					if (e.Converted.IsValueCreated && !e.Converted.Value.IsUpToDate) {
						entries.Remove(node);
						break;
					}
					entries.Remove(node);
					entries.AddFirst(node);
					entry = e;
					break;
				}
				if (entry is null) {
					entry = new Entry(module, inMemory);
					entries.AddFirst(entry);
				}
			}

			// Created outside the lock so other modules can be converted at the same time
			ConvertedModule converted;
			try {
				converted = entry.Converted.Value;
			}
			catch {
				lock (lockObj)
					entries.Remove(entry);
				throw;
			}
			lock (lockObj) {
				if (inMemory)
					Trim(entries, MaxMainModules, long.MaxValue);
				else
					Trim(entries, MaxReferencedModules, MaxReferencedModulesBytes);
			}
			return converted;
		}

		// Removes the least recently used entries, but always keeps the first one
		static void Trim(LinkedList<Entry> entries, int maxCount, long maxBytes) {
			long bytes = 0;
			int count = 0;
			for (var node = entries.First; node is not null;) {
				var next = node.Next;
				count++;
				bytes += node.Value.ImageSize;
				if (count > 1 && (count > maxCount || bytes > maxBytes)) {
					entries.Remove(node);
					count--;
					bytes -= node.Value.ImageSize;
				}
				node = next;
			}
		}

		/// <summary>
		/// Removes the converted copies of <paramref name="module"/>. Call it after the module has been modified.
		/// </summary>
		/// <param name="module">Modified module</param>
		public static void OnModuleModified(ModuleDef module) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			lock (lockObj) {
				Remove(mainEntries, module);
				Remove(referencedEntries, module);
			}
		}

		static void Remove(LinkedList<Entry> entries, ModuleDef module) {
			for (var node = entries.First; node is not null;) {
				var next = node.Next;
				if (node.Value.Module == module)
					entries.Remove(node);
				node = next;
			}
		}

		/// <summary>
		/// Removes all converted modules
		/// </summary>
		public static void Clear() {
			lock (lockObj) {
				mainEntries.Clear();
				referencedEntries.Clear();
			}
		}
	}
}
