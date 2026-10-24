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
		static readonly Guid LanguageVersion_GUID = new Guid("2F8E6A1C-4D3B-4B95-A7E2-9C0D5B3F6E18");
		const string LanguageVersion_NAME = "csharp-version";

		/// <summary>
		/// Value of <see cref="LanguageVersion"/> that enables every feature ILSpy supports
		/// </summary>
		public const string LatestLanguageVersion = "latest";

		/// <summary>
		/// All values of <see cref="LanguageVersion"/>, oldest first, ending with <see cref="LatestLanguageVersion"/>
		/// </summary>
		public static IReadOnlyList<string> LanguageVersions { get; } = new[] {
			"1", "2", "3", "4", "5", "6", "7", "7.1", "7.2", "7.3", "8.0", "9.0", "10.0", "11.0", "12.0", "13.0", "14.0", LatestLanguageVersion,
		};

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
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowTokenAndRvaComments_GUID, () => ShowTokenAndRvaComments, a => ShowTokenAndRvaComments = a) {
				Description = "Show tokens, RVAs and file offsets in comments",
				Name = DecompilerOptionConstants.ShowTokenAndRvaComments_NAME,
			};
			yield return new DecompilerOption<string>(LanguageVersion_GUID, () => LanguageVersion, a => LanguageVersion = a) {
				Description = "C# language version (" + string.Join(", ", LanguageVersions) + ")",
				Name = LanguageVersion_NAME,
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
		/// Write a <c>// Token: ... RVA: ... File Offset: ...</c> comment before each definition (like the old C# language)
		/// </summary>
		public bool ShowTokenAndRvaComments {
			get => showTokenAndRvaComments;
			set => SetValue(ref showTokenAndRvaComments, value, nameof(ShowTokenAndRvaComments));
		}
		bool showTokenAndRvaComments = true;

		/// <summary>
		/// Run dnSpy's own AST transforms after ILSpy's (they're what makes the output closer to the original source)
		/// </summary>
		public bool CleanUpOutput {
			get => cleanUpOutput;
			set => SetValue(ref cleanUpOutput, value, nameof(CleanUpOutput));
		}
		bool cleanUpOutput = true;

		/// <summary>
		/// The newest C# version the output may use, one of <see cref="LanguageVersions"/>. Other values
		/// (eg. "8" or "C#12") are converted to the matching value; unknown values become <see cref="LatestLanguageVersion"/>.
		/// </summary>
		public string LanguageVersion {
			get => languageVersion;
			set {
				var newValue = NormalizeLanguageVersion(value);
				if (languageVersion == newValue)
					return;
				languageVersion = newValue;
				OnValueChanged(nameof(LanguageVersion));
			}
		}
		string languageVersion = LatestLanguageVersion;

		static string NormalizeLanguageVersion(string? value) {
			var s = (value ?? string.Empty).Trim();
			if (s.StartsWith("C#", StringComparison.OrdinalIgnoreCase))
				s = s.Substring(2).Trim();
			if (s.StartsWith("CSharp", StringComparison.OrdinalIgnoreCase))
				s = s.Substring(6).Replace('_', '.').Trim();
			foreach (var v in LanguageVersions) {
				if (StringComparer.OrdinalIgnoreCase.Equals(v, s))
					return v;
			}
			// "8" => "8.0", "7.0" => "7"
			foreach (var v in LanguageVersions) {
				if (v + ".0" == s || s + ".0" == v)
					return v;
			}
			return LatestLanguageVersion;
		}

		static ICSharpCode.Decompiler.CSharp.LanguageVersion ToILSpyLanguageVersion(string value) {
			if (value == LatestLanguageVersion)
				return ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest;
			// ILSpy: CSharp7 = 7, CSharp7_3 = 703, CSharp8_0 = 800
			var parts = value.Split('.');
			int major = int.Parse(parts[0]);
			int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
			return (ICSharpCode.Decompiler.CSharp.LanguageVersion)(major <= 7 && minor == 0 ? major : major * 100 + minor);
		}

		void SetValue(ref bool field, bool value, string propertyName) {
			if (field == value)
				return;
			field = value;
			OnValueChanged(propertyName);
		}

		void OnValueChanged(string propertyName) {
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
			other.ShowTokenAndRvaComments = ShowTokenAndRvaComments;
			other.CleanUpOutput = CleanUpOutput;
			other.LanguageVersion = LanguageVersion;
			return other;
		}

		internal ILSpyDecompilerSettings CreateILSpySettings() {
			// The ctor disables the features that are newer than the language version, so only turn off more features here
			var settings = new ILSpyDecompilerSettings(ToILSpyLanguageVersion(LanguageVersion)) {
				ThrowOnAssemblyResolveErrors = false,
				UsingDeclarations = UsingDeclarations,
				UseDebugSymbols = UseDebugSymbols,
				ShowXmlDocumentation = ShowXmlDocumentation,
			};
			settings.ExpressionTrees &= ExpressionTrees;
			settings.YieldReturn &= YieldReturn;
			settings.AsyncAwait &= AsyncAwait;
			settings.SwitchExpressions &= SwitchExpressions;
			settings.StringInterpolation &= StringInterpolation;
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
			ShowTokenAndRvaComments == other.ShowTokenAndRvaComments &&
			CleanUpOutput == other.CleanUpOutput &&
			LanguageVersion == other.LanguageVersion;

		public override int GetHashCode() {
			int h = 0;
			foreach (var opt in options) {
				if (opt.Value is bool b)
					h = (h << 1) | (b ? 1 : 0);
			}
			return h ^ LanguageVersion.GetHashCode();
		}
	}
}
