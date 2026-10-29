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
using dnSpy.Decompiler.ILSpyModern.Core.Properties;
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
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileAnonMethods,
				Name = DecompilerOptionConstants.AnonymousMethods_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ExpressionTrees_GUID, () => ExpressionTrees, a => ExpressionTrees = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileExprTrees,
				Name = DecompilerOptionConstants.ExpressionTrees_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.YieldReturn_GUID, () => YieldReturn, a => YieldReturn = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileEnumerators,
				Name = DecompilerOptionConstants.YieldReturn_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.AsyncAwait_GUID, () => AsyncAwait, a => AsyncAwait = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileAsyncMethods,
				Name = DecompilerOptionConstants.AsyncAwait_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.QueryExpressions_GUID, () => QueryExpressions, a => QueryExpressions = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileQueryExpr,
				Name = DecompilerOptionConstants.QueryExpressions_NAME,
			};
			yield return new DecompilerOption<bool>(SwitchExpressions_GUID, () => SwitchExpressions, a => SwitchExpressions = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileSwitchExpressions,
				Name = SwitchExpressions_NAME,
			};
			yield return new DecompilerOption<bool>(StringInterpolation_GUID, () => StringInterpolation, a => StringInterpolation = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_UseStringInterpolation,
				Name = StringInterpolation_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.AutomaticProperties_GUID, () => AutomaticProperties, a => AutomaticProperties = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileAutoProps,
				Name = DecompilerOptionConstants.AutomaticProperties_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.AutomaticEvents_GUID, () => AutomaticEvents, a => AutomaticEvents = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileAutoEvents,
				Name = DecompilerOptionConstants.AutomaticEvents_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.UsingStatement_GUID, () => UsingStatement, a => UsingStatement = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileUsingStatements,
				Name = DecompilerOptionConstants.UsingStatement_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ForEachStatement_GUID, () => ForEachStatement, a => ForEachStatement = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileForeachStatements,
				Name = DecompilerOptionConstants.ForEachStatement_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.LockStatement_GUID, () => LockStatement, a => LockStatement = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompileLockStatements,
				Name = DecompilerOptionConstants.LockStatement_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ObjectOrCollectionInitializers_GUID, () => ObjectOrCollectionInitializers, a => ObjectOrCollectionInitializers = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_ObjectOrCollectionInitializers,
				Name = DecompilerOptionConstants.ObjectOrCollectionInitializers_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.IntroduceIncrementAndDecrement_GUID, () => IntroduceIncrementAndDecrement, a => IntroduceIncrementAndDecrement = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_IntroduceIncrementAndDecrement,
				Name = DecompilerOptionConstants.IntroduceIncrementAndDecrement_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.MakeAssignmentExpressions_GUID, () => MakeAssignmentExpressions, a => MakeAssignmentExpressions = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_MakeAssignmentExpressions,
				Name = DecompilerOptionConstants.MakeAssignmentExpressions_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.SortCustomAttributes_GUID, () => SortCustomAttributes, a => SortCustomAttributes = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_SortCustomAttributes,
				Name = DecompilerOptionConstants.SortCustomAttributes_NAME,
			};
			yield return new DecompilerOption<string>(DecompilerOptionConstants.MemberOrder_GUID, () => MemberOrder, a => MemberOrder = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_DecompilationOrder + " (" + dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_MemberOrderKinds + ")",
				Name = DecompilerOptionConstants.MemberOrder_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.SortMembers_GUID, () => SortMembers, a => SortMembers = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_SortMethods,
				Name = DecompilerOptionConstants.SortMembers_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.UseSourceCodeOrder_GUID, () => UseSourceCodeOrder, a => UseSourceCodeOrder = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_UseSourceCodeOrder,
				Name = DecompilerOptionConstants.UseSourceCodeOrder_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.UsingDeclarations_GUID, () => UsingDeclarations, a => UsingDeclarations = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_AddUsingDeclarations,
				Name = DecompilerOptionConstants.UsingDeclarations_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.UseDebugSymbols_GUID, () => UseDebugSymbols, a => UseDebugSymbols = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_UseLocalNameFromSyms,
				Name = DecompilerOptionConstants.UseDebugSymbols_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowXmlDocumentation_GUID, () => ShowXmlDocumentation, a => ShowXmlDocumentation = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_ShowXMLDocComments,
				Name = DecompilerOptionConstants.ShowXmlDocumentation_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowTokenAndRvaComments_GUID, () => ShowTokenAndRvaComments, a => ShowTokenAndRvaComments = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_ShowTokensRvasOffsets,
				Name = DecompilerOptionConstants.ShowTokenAndRvaComments_NAME,
			};
			yield return new DecompilerOption<string>(LanguageVersion_GUID, () => LanguageVersion, a => LanguageVersion = a) {
				Description = string.Format(dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_LanguageVersion, string.Join(", ", LanguageVersions)),
				Name = LanguageVersion_NAME,
			};
			yield return new DecompilerOption<bool>(CleanUpOutput_GUID, () => CleanUpOutput, a => CleanUpOutput = a) {
				Description = dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilerSettings_CleanUpOutput,
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

		public bool AutomaticProperties {
			get => automaticProperties;
			set => SetValue(ref automaticProperties, value, nameof(AutomaticProperties));
		}
		bool automaticProperties = true;

		public bool AutomaticEvents {
			get => automaticEvents;
			set => SetValue(ref automaticEvents, value, nameof(AutomaticEvents));
		}
		bool automaticEvents = true;

		public bool UsingStatement {
			get => usingStatement;
			set => SetValue(ref usingStatement, value, nameof(UsingStatement));
		}
		bool usingStatement = true;

		public bool ForEachStatement {
			get => forEachStatement;
			set => SetValue(ref forEachStatement, value, nameof(ForEachStatement));
		}
		bool forEachStatement = true;

		public bool LockStatement {
			get => lockStatement;
			set => SetValue(ref lockStatement, value, nameof(LockStatement));
		}
		bool lockStatement = true;

		public bool ObjectOrCollectionInitializers {
			get => objectOrCollectionInitializers;
			set => SetValue(ref objectOrCollectionInitializers, value, nameof(ObjectOrCollectionInitializers));
		}
		bool objectOrCollectionInitializers = true;

		public bool IntroduceIncrementAndDecrement {
			get => introduceIncrementAndDecrement;
			set => SetValue(ref introduceIncrementAndDecrement, value, nameof(IntroduceIncrementAndDecrement));
		}
		bool introduceIncrementAndDecrement = true;

		public bool MakeAssignmentExpressions {
			get => makeAssignmentExpressions;
			set => SetValue(ref makeAssignmentExpressions, value, nameof(MakeAssignmentExpressions));
		}
		bool makeAssignmentExpressions = true;

		public bool SortCustomAttributes {
			get => sortCustomAttributes;
			set => SetValue(ref sortCustomAttributes, value, nameof(SortCustomAttributes));
		}
		bool sortCustomAttributes = false;

		/// <summary>
		/// Default value of <see cref="MemberOrder"/>, ILSpy's order: nested types, fields, properties, events, methods
		/// </summary>
		public const string DefaultMemberOrder = "tfpem";

		/// <summary>
		/// Order of the member kinds in a type, a permutation of <see cref="DefaultMemberOrder"/> (same as the old
		/// C# language's option: t = nested types, f = fields, p = properties, e = events, m = methods). Other values are ignored.
		/// </summary>
		public string MemberOrder {
			get => memberOrder;
			set {
				if (value is null || value.Length != DefaultMemberOrder.Length || !value.OrderBy(c => c).SequenceEqual(DefaultMemberOrder.OrderBy(c => c)))
					return;
				if (memberOrder == value)
					return;
				memberOrder = value;
				OnValueChanged(nameof(MemberOrder));
			}
		}
		string memberOrder = DefaultMemberOrder;

		/// <summary>
		/// Sort the members of each kind by name (not the fields of types with a sequential or explicit layout)
		/// </summary>
		public bool SortMembers {
			get => sortMembers;
			set => SetValue(ref sortMembers, value, nameof(SortMembers));
		}
		bool sortMembers = false;

		/// <summary>
		/// Write methods, properties and events together in metadata order (a property or event at its first accessor),
		/// at the position of the first of them in <see cref="MemberOrder"/>. Overrides <see cref="SortMembers"/>.
		/// </summary>
		public bool UseSourceCodeOrder {
			get => useSourceCodeOrder;
			set => SetValue(ref useSourceCodeOrder, value, nameof(UseSourceCodeOrder));
		}
		bool useSourceCodeOrder = false;

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
			other.AutomaticProperties = AutomaticProperties;
			other.AutomaticEvents = AutomaticEvents;
			other.UsingStatement = UsingStatement;
			other.ForEachStatement = ForEachStatement;
			other.LockStatement = LockStatement;
			other.ObjectOrCollectionInitializers = ObjectOrCollectionInitializers;
			other.IntroduceIncrementAndDecrement = IntroduceIncrementAndDecrement;
			other.MakeAssignmentExpressions = MakeAssignmentExpressions;
			other.SortCustomAttributes = SortCustomAttributes;
			other.MemberOrder = MemberOrder;
			other.SortMembers = SortMembers;
			other.UseSourceCodeOrder = UseSourceCodeOrder;
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
				SortCustomAttributes = SortCustomAttributes,
			};
			settings.ExpressionTrees &= ExpressionTrees;
			settings.YieldReturn &= YieldReturn;
			settings.AsyncAwait &= AsyncAwait;
			settings.SwitchExpressions &= SwitchExpressions;
			settings.StringInterpolation &= StringInterpolation;
			settings.AutomaticProperties &= AutomaticProperties;
			settings.AutomaticEvents &= AutomaticEvents;
			settings.UsingStatement &= UsingStatement;
			settings.ForEachStatement &= ForEachStatement;
			settings.LockStatement &= LockStatement;
			settings.ObjectOrCollectionInitializers &= ObjectOrCollectionInitializers;
			settings.IntroduceIncrementAndDecrement &= IntroduceIncrementAndDecrement;
			settings.MakeAssignmentExpressions &= MakeAssignmentExpressions;
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
			AutomaticProperties == other.AutomaticProperties &&
			AutomaticEvents == other.AutomaticEvents &&
			UsingStatement == other.UsingStatement &&
			ForEachStatement == other.ForEachStatement &&
			LockStatement == other.LockStatement &&
			ObjectOrCollectionInitializers == other.ObjectOrCollectionInitializers &&
			IntroduceIncrementAndDecrement == other.IntroduceIncrementAndDecrement &&
			MakeAssignmentExpressions == other.MakeAssignmentExpressions &&
			SortCustomAttributes == other.SortCustomAttributes &&
			MemberOrder == other.MemberOrder &&
			SortMembers == other.SortMembers &&
			UseSourceCodeOrder == other.UseSourceCodeOrder &&
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
			return h ^ LanguageVersion.GetHashCode() ^ MemberOrder.GetHashCode();
		}
	}
}
