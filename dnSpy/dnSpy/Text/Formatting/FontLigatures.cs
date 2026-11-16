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

using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using dnSpy.Contracts.Extension;
using dnSpy.MainApp;
using Microsoft.VisualStudio.Text.Editor;

// Options > Environment > Font: "Use font ligatures". Fonts such as Cascadia Code and Fira Code draw eg. "=>" and
// "!=" as one symbol; WPF does that by default. With the option off, the text views' text gets typography with
// ligatures and contextual alternates (which those fonts use for their ligatures) turned off.
namespace dnSpy.Text.Formatting {
	static class FontLigatures {
		public const string UseFontLigaturesName = "dnSpy-UseFontLigatures";
		public static readonly EditorOptionKey<bool> UseFontLigaturesId = new EditorOptionKey<bool>(UseFontLigaturesName);

		/// <summary>The option's value; the text views read it when they format their lines</summary>
		public static bool Enabled { get; set; } = true;

		static readonly ConditionalWeakTable<TextRunProperties, TextRunProperties> noLigatures = new ConditionalWeakTable<TextRunProperties, TextRunProperties>();

		/// <summary><paramref name="props"/>, or a copy without ligatures if they're turned off</summary>
		public static TextRunProperties Apply(TextRunProperties props) {
			if (Enabled)
				return props;
			return noLigatures.GetValue(props, p => new NoLigaturesTextRunProperties(p));
		}

		sealed class NoLigaturesTextRunProperties : TextRunProperties {
			readonly TextRunProperties props;
			public NoLigaturesTextRunProperties(TextRunProperties props) => this.props = props;
			public override Typeface Typeface => props.Typeface;
			public override double FontRenderingEmSize => props.FontRenderingEmSize;
			public override double FontHintingEmSize => props.FontHintingEmSize;
			public override TextDecorationCollection TextDecorations => props.TextDecorations;
			public override Brush ForegroundBrush => props.ForegroundBrush;
			public override Brush BackgroundBrush => props.BackgroundBrush;
			public override CultureInfo CultureInfo => props.CultureInfo;
			public override TextEffectCollection TextEffects => props.TextEffects;
			public override BaselineAlignment BaselineAlignment => props.BaselineAlignment;
			public override NumberSubstitution NumberSubstitution => props.NumberSubstitution;
			public override TextRunTypographyProperties TypographyProperties => NoLigaturesTypography.Instance;
		}

		// WPF's defaults (Typography's), except the ligatures and contextual alternates
		sealed class NoLigaturesTypography : TextRunTypographyProperties {
			public static readonly NoLigaturesTypography Instance = new NoLigaturesTypography();
			public override bool StandardLigatures => false;
			public override bool ContextualLigatures => false;
			public override bool DiscretionaryLigatures => false;
			public override bool HistoricalLigatures => false;
			public override bool ContextualAlternates => false;
			public override bool Kerning => true;
			public override bool CaseSensitiveForms => false;
			public override int ContextualSwashes => 0;
			public override bool EastAsianExpertForms => false;
			public override bool HistoricalForms => false;
			public override bool MathematicalGreek => false;
			public override bool SlashedZero => false;
			public override int AnnotationAlternates => 0;
			public override FontCapitals Capitals => FontCapitals.Normal;
			public override bool CapitalSpacing => false;
			public override FontEastAsianLanguage EastAsianLanguage => FontEastAsianLanguage.Normal;
			public override FontEastAsianWidths EastAsianWidths => FontEastAsianWidths.Normal;
			public override FontFraction Fraction => FontFraction.Normal;
			public override FontNumeralAlignment NumeralAlignment => FontNumeralAlignment.Normal;
			public override FontNumeralStyle NumeralStyle => FontNumeralStyle.Normal;
			public override int StandardSwashes => 0;
			public override int StylisticAlternates => 0;
			public override bool StylisticSet1 => false;
			public override bool StylisticSet2 => false;
			public override bool StylisticSet3 => false;
			public override bool StylisticSet4 => false;
			public override bool StylisticSet5 => false;
			public override bool StylisticSet6 => false;
			public override bool StylisticSet7 => false;
			public override bool StylisticSet8 => false;
			public override bool StylisticSet9 => false;
			public override bool StylisticSet10 => false;
			public override bool StylisticSet11 => false;
			public override bool StylisticSet12 => false;
			public override bool StylisticSet13 => false;
			public override bool StylisticSet14 => false;
			public override bool StylisticSet15 => false;
			public override bool StylisticSet16 => false;
			public override bool StylisticSet17 => false;
			public override bool StylisticSet18 => false;
			public override bool StylisticSet19 => false;
			public override bool StylisticSet20 => false;
			public override FontVariants Variants => FontVariants.Normal;
		}
	}

	[Export(typeof(EditorOptionDefinition))]
	sealed class UseFontLigaturesEditorOptionDefinition : EditorOptionDefinition<bool> {
		public override EditorOptionKey<bool> Key => FontLigatures.UseFontLigaturesId;
		public override bool Default => true;
	}

	/// <summary>
	/// Keeps the global editor option (and so every text view) in sync with the setting
	/// </summary>
	[ExportAutoLoaded]
	sealed class FontLigaturesSettingsLoader : IAutoLoaded {
		readonly AppSettingsImpl appSettings;
		readonly IEditorOptionsFactoryService editorOptionsFactoryService;

		[ImportingConstructor]
		FontLigaturesSettingsLoader(AppSettingsImpl appSettings, IEditorOptionsFactoryService editorOptionsFactoryService) {
			this.appSettings = appSettings;
			this.editorOptionsFactoryService = editorOptionsFactoryService;
			Update();
			appSettings.PropertyChanged += AppSettings_PropertyChanged;
		}

		void AppSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == nameof(AppSettings.UseFontLigatures))
				Update();
		}

		void Update() {
			FontLigatures.Enabled = appSettings.UseFontLigatures;
			editorOptionsFactoryService.GlobalOptions.SetOptionValue(FontLigatures.UseFontLigaturesId, appSettings.UseFontLigatures);
		}
	}
}
