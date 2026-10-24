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
using System.ComponentModel;
using System.Linq;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.Settings;
using ILSpyDecompilerSettings = ICSharpCode.Decompiler.DecompilerSettings;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Settings of the C# (ILSpy 11) language. They're shown in the settings dialog and can be set with dnSpy.Console's options.
	/// </summary>
	public class CSharpDecompilerSettings : DecompilerSettingsBase, INotifyPropertyChanged {
		// Options that the old C# language doesn't have
		static readonly Guid SwitchExpressions_GUID = new Guid("5D3C1B0E-8F7A-4E21-9C55-0B4E7A1D2F61");
		const string SwitchExpressions_NAME = "switch-expr";
		static readonly Guid StringInterpolation_GUID = new Guid("A1E4F2C3-6B7D-4A89-8E10-3F5C2D9B7A42");
		const string StringInterpolation_NAME = "interpolation";
		static readonly Guid CleanUpOutput_GUID = new Guid("C7B2E9D4-1A3F-4C6E-B8D0-5E2A9F4C1B73");
		const string CleanUpOutput_NAME = "dnspy-cleanup";

		public override int Version => settingsVersion;
		volatile int settingsVersion;

		public override event EventHandler? VersionChanged;
		public event PropertyChangedEventHandler? PropertyChanged;

		public CSharpDecompilerSettings() => options = CreateOptions().ToArray();

		public override IEnumerable<IDecompilerOption> Options => options;
		readonly IDecompilerOption[] options;

		IEnumerable<IDecompilerOption> CreateOptions() {
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.AnonymousMethods_GUID, () => AnonymousMethods, a => AnonymousMethods = a) {
				Description = "Decompile anonymous methods/lambdas",
				Name = DecompilerOptionConstants.AnonymousMethods_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ExpressionTrees_GUID, () => ExpressionTrees, a => ExpressionTrees = a) {
				Description = "Decompile expression trees",
				Name = DecompilerOptionConstants.ExpressionTrees_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.YieldReturn_GUID, () => YieldReturn, a => YieldReturn = a) {
				Description = "Decompile enumerators (yield return)",
				Name = DecompilerOptionConstants.YieldReturn_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.AsyncAwait_GUID, () => AsyncAwait, a => AsyncAwait = a) {
				Description = "Decompile async methods (async/await)",
				Name = DecompilerOptionConstants.AsyncAwait_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.QueryExpressions_GUID, () => QueryExpressions, a => QueryExpressions = a) {
				Description = "Decompile query expressions",
				Name = DecompilerOptionConstants.QueryExpressions_NAME,
			};
			yield return new DecompilerOption<bool>(SwitchExpressions_GUID, () => SwitchExpressions, a => SwitchExpressions = a) {
				Description = "Decompile switch expressions",
				Name = SwitchExpressions_NAME,
			};
			yield return new DecompilerOption<bool>(StringInterpolation_GUID, () => StringInterpolation, a => StringInterpolation = a) {
				Description = "Use string interpolation",
				Name = StringInterpolation_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.UsingDeclarations_GUID, () => UsingDeclarations, a => UsingDeclarations = a) {
				Description = "Add using declarations",
				Name = DecompilerOptionConstants.UsingDeclarations_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.UseDebugSymbols_GUID, () => UseDebugSymbols, a => UseDebugSymbols = a) {
				Description = "Use variable names from debug symbols, if available",
				Name = DecompilerOptionConstants.UseDebugSymbols_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowXmlDocumentation_GUID, () => ShowXmlDocumentation, a => ShowXmlDocumentation = a) {
				Description = "Show XML documentation in decompiled code",
				Name = DecompilerOptionConstants.ShowXmlDocumentation_NAME,
			};
			yield return new DecompilerOption<bool>(CleanUpOutput_GUID, () => CleanUpOutput, a => CleanUpOutput = a) {
				Description = "Clean up ILSpy's output (dnSpy: switch expressions, string interpolation, decimal casts, empty if statements, lambda temporaries)",
				Name = CleanUpOutput_NAME,
			};
		}

		public bool AnonymousMethods {
			get => anonymousMethods;
			set => SetValue(ref anonymousMethods, value, nameof(AnonymousMethods));
		}
		bool anonymousMethods = true;

		public bool ExpressionTrees {
			get => expressionTrees;
			set => SetValue(ref expressionTrees, value, nameof(ExpressionTrees));
		}
		bool expressionTrees = true;

		public bool YieldReturn {
			get => yieldReturn;
			set => SetValue(ref yieldReturn, value, nameof(YieldReturn));
		}
		bool yieldReturn = true;

		public bool AsyncAwait {
			get => asyncAwait;
			set => SetValue(ref asyncAwait, value, nameof(AsyncAwait));
		}
		bool asyncAwait = true;

		public bool QueryExpressions {
			get => queryExpressions;
			set => SetValue(ref queryExpressions, value, nameof(QueryExpressions));
		}
		bool queryExpressions = true;

		public bool SwitchExpressions {
			get => switchExpressions;
			set => SetValue(ref switchExpressions, value, nameof(SwitchExpressions));
		}
		bool switchExpressions = true;

		public bool StringInterpolation {
			get => stringInterpolation;
			set => SetValue(ref stringInterpolation, value, nameof(StringInterpolation));
		}
		bool stringInterpolation = true;

		public bool UsingDeclarations {
			get => usingDeclarations;
			set => SetValue(ref usingDeclarations, value, nameof(UsingDeclarations));
		}
		bool usingDeclarations = true;

		public bool UseDebugSymbols {
			get => useDebugSymbols;
			set => SetValue(ref useDebugSymbols, value, nameof(UseDebugSymbols));
		}
		bool useDebugSymbols = true;

		public bool ShowXmlDocumentation {
			get => showXmlDocumentation;
			set => SetValue(ref showXmlDocumentation, value, nameof(ShowXmlDocumentation));
		}
		bool showXmlDocumentation = true;

		/// <summary>
		/// Run dnSpy's own AST transforms after ILSpy's (they're what makes the output closer to the original source)
		/// </summary>
		public bool CleanUpOutput {
			get => cleanUpOutput;
			set => SetValue(ref cleanUpOutput, value, nameof(CleanUpOutput));
		}
		bool cleanUpOutput = true;

		void SetValue(ref bool field, bool value, string propertyName) {
			if (field == value)
				return;
			field = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
			settingsVersion++;
			OnModified();
			VersionChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>
		/// Called after a setting has changed
		/// </summary>
		protected virtual void OnModified() { }

		public override DecompilerSettingsBase Clone() => CopyTo(new CSharpDecompilerSettings());

		public CSharpDecompilerSettings CopyTo(CSharpDecompilerSettings other) {
			other.AnonymousMethods = AnonymousMethods;
			other.ExpressionTrees = ExpressionTrees;
			other.YieldReturn = YieldReturn;
			other.AsyncAwait = AsyncAwait;
			other.QueryExpressions = QueryExpressions;
			other.SwitchExpressions = SwitchExpressions;
			other.StringInterpolation = StringInterpolation;
			other.UsingDeclarations = UsingDeclarations;
			other.UseDebugSymbols = UseDebugSymbols;
			other.ShowXmlDocumentation = ShowXmlDocumentation;
			other.CleanUpOutput = CleanUpOutput;
			return other;
		}

		internal ILSpyDecompilerSettings CreateILSpySettings() {
			var settings = new ILSpyDecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest) {
				ThrowOnAssemblyResolveErrors = false,
				ExpressionTrees = ExpressionTrees,
				YieldReturn = YieldReturn,
				AsyncAwait = AsyncAwait,
				SwitchExpressions = SwitchExpressions,
				StringInterpolation = StringInterpolation,
				UsingDeclarations = UsingDeclarations,
				UseDebugSymbols = UseDebugSymbols,
				ShowXmlDocumentation = ShowXmlDocumentation,
			};
			// ILSpy's query expressions need lambdas
			if (!AnonymousMethods) {
				settings.AnonymousMethods = false;
				settings.LocalFunctions = false;
			}
			if (!AnonymousMethods || !QueryExpressions)
				settings.QueryExpressions = false;
			return settings;
		}

		public override bool Equals(object? obj) =>
			obj is CSharpDecompilerSettings other &&
			AnonymousMethods == other.AnonymousMethods &&
			ExpressionTrees == other.ExpressionTrees &&
			YieldReturn == other.YieldReturn &&
			AsyncAwait == other.AsyncAwait &&
			QueryExpressions == other.QueryExpressions &&
			SwitchExpressions == other.SwitchExpressions &&
			StringInterpolation == other.StringInterpolation &&
			UsingDeclarations == other.UsingDeclarations &&
			UseDebugSymbols == other.UseDebugSymbols &&
			ShowXmlDocumentation == other.ShowXmlDocumentation &&
			CleanUpOutput == other.CleanUpOutput;

		public override int GetHashCode() {
			int h = 0;
			foreach (var opt in options)
				h = (h << 1) | ((bool)opt.Value! ? 1 : 0);
			return h;
		}
	}
}
