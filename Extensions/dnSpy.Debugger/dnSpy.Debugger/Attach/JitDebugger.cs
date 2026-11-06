/*
    Copyright (C) 2026- marinusmaurice@outlook.com

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
using System.IO;
using System.Runtime.InteropServices;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Menus;
using dnSpy.Debugger.Properties;
using Microsoft.Win32;

// dnSpy as the just-in-time debugger: Debugger.Launch() (and a crash, if Windows is set to ask) starts
// dnSpy with the process ID and an event handle; dnSpy asks whether to attach and then sets the event
// so the process continues. Registered in the same places as Visual Studio's JIT debugger:
//   .NET (Core) and native: HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug, Debugger
//   .NET Framework: HKLM\SOFTWARE\Microsoft\.NETFramework, DbgManagedDebugger
// both in the 64-bit and the 32-bit registry view. The old values are kept next to them and put back
// when dnSpy is unregistered.
namespace dnSpy.Debugger.Attach {
	static class JitDebuggerRegistration {
		const string AeDebugKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug";
		const string AeDebugValue = "Debugger";
		const string NetFrameworkKey = @"SOFTWARE\Microsoft\.NETFramework";
		const string NetFrameworkValue = "DbgManagedDebugger";
		const string PreviousSuffix = ".dnSpyPrevious";

		/// <summary>
		/// The dnSpy exe for 64-bit (or 32-bit) processes, or null if there's none (eg. the .NET x64 zip has no 32-bit dnSpy)
		/// </summary>
		static string? GetExe(bool is64Bit) {
			var exe = Process.GetCurrentProcess().MainModule?.FileName;
			if (exe is null)
				return null;
			bool isCurrent64 = Environment.Is64BitProcess;
			if (is64Bit == isCurrent64)
				return exe;
			var dir = Path.GetDirectoryName(exe)!;
			var other = is64Bit ? Path.Combine(dir, "dnSpy.exe") : Path.Combine(dir, "dnSpy-x86.exe");
			return File.Exists(other) && !StringComparer.OrdinalIgnoreCase.Equals(other, exe) ? other : null;
		}

		// .NET (Core) and Windows pass: process ID, event handle, JIT_DEBUG_INFO address
		static string GetAeDebugCommand(string exe) => "\"" + exe + "\" -p %ld -e %ld --jdinfo %p";
		// .NET Framework passes: process ID, AppDomain ID, a text, event handle
		static string GetNetFrameworkCommand(string exe) => "\"" + exe + "\" -p %d --appdomain %d --exttext \"%s\" -e %d";

		public static bool IsRegistered {
			get {
				try {
					using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32).OpenSubKey(AeDebugKey);
					var exe = GetExe(Environment.Is64BitOperatingSystem);
					return exe is not null && key?.GetValue(AeDebugValue) is string s && s.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
				}
				catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) {
					return false;
				}
			}
		}

		/// <summary>
		/// Registers or unregisters dnSpy. Needs admin rights; returns an error message or null.
		/// </summary>
		public static string? SetRegistered(bool register) {
			try {
				foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 }) {
					bool is64 = view == RegistryView.Registry64;
					var exe = GetExe(is64);
					using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
					Set(baseKey, AeDebugKey, AeDebugValue, register && exe is not null ? GetAeDebugCommand(exe) : null);
					Set(baseKey, NetFrameworkKey, NetFrameworkValue, register && exe is not null ? GetNetFrameworkCommand(exe) : null);
				}
				return null;
			}
			catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) {
				return string.Format(dnSpy_Debugger_Resources.JitDebugger_RegistryError, ex.Message);
			}
		}

		// command null: put back the previous value (if dnSpy is the registered debugger)
		static void Set(RegistryKey baseKey, string keyName, string valueName, string? command) {
			using var key = baseKey.CreateSubKey(keyName, writable: true);
			var current = key.GetValue(valueName) as string;
			bool isDnSpy = current is not null && current.IndexOf("dnSpy", StringComparison.OrdinalIgnoreCase) >= 0 && current.IndexOf(" -p %", StringComparison.Ordinal) >= 0;
			if (command is not null) {
				if (!isDnSpy) {
					if (current is null)
						key.DeleteValue(valueName + PreviousSuffix, throwOnMissingValue: false);
					else
						key.SetValue(valueName + PreviousSuffix, current);
				}
				key.SetValue(valueName, command);
			}
			else if (isDnSpy) {
				if (key.GetValue(valueName + PreviousSuffix) is string previous)
					key.SetValue(valueName, previous);
				else
					key.DeleteValue(valueName, throwOnMissingValue: false);
				key.DeleteValue(valueName + PreviousSuffix, throwOnMissingValue: false);
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetEvent(IntPtr hEvent);
		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool CloseHandle(IntPtr hObject);

		/// <summary>
		/// Lets the process that started dnSpy as its JIT debugger continue (the event handle was inherited)
		/// </summary>
		public static void SignalEvent(uint handle) {
			if (handle == 0)
				return;
			var h = new IntPtr(handle);
			SetEvent(h);
			CloseHandle(h);
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_DEBUG_GUID, Header = "res:JitDebuggerCommand", Group = MenuConstants.GROUP_APP_MENU_DEBUG_START, Order = 40)]
	sealed class JitDebuggerMenuCommand : MenuItemBase {
		readonly IMessageBoxService messageBoxService;

		[ImportingConstructor]
		JitDebuggerMenuCommand(IMessageBoxService messageBoxService) => this.messageBoxService = messageBoxService;

		public override bool IsChecked(IMenuItemContext context) => JitDebuggerRegistration.IsRegistered;

		public override void Execute(IMenuItemContext context) {
			var error = JitDebuggerRegistration.SetRegistered(!JitDebuggerRegistration.IsRegistered);
			if (error is not null)
				messageBoxService.Show(error);
		}
	}
}
