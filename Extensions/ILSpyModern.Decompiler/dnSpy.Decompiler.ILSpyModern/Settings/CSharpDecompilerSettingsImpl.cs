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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings;
using dnSpy.Decompiler.ILSpyModern.Core;

namespace dnSpy.Decompiler.ILSpyModern.Settings {
	[Export]
	sealed class CSharpDecompilerSettingsImpl : CSharpDecompilerSettings {
		static readonly Guid SETTINGS_GUID = new Guid("3B0F4C2E-9D61-4E7A-A5B8-6C1D2E3F4A95");

		readonly ISettingsService settingsService;

		[ImportingConstructor]
		CSharpDecompilerSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			disableSave = true;
			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			AnonymousMethods = sect.Attribute<bool?>(nameof(AnonymousMethods)) ?? AnonymousMethods;
			ExpressionTrees = sect.Attribute<bool?>(nameof(ExpressionTrees)) ?? ExpressionTrees;
			YieldReturn = sect.Attribute<bool?>(nameof(YieldReturn)) ?? YieldReturn;
			AsyncAwait = sect.Attribute<bool?>(nameof(AsyncAwait)) ?? AsyncAwait;
			QueryExpressions = sect.Attribute<bool?>(nameof(QueryExpressions)) ?? QueryExpressions;
			SwitchExpressions = sect.Attribute<bool?>(nameof(SwitchExpressions)) ?? SwitchExpressions;
			StringInterpolation = sect.Attribute<bool?>(nameof(StringInterpolation)) ?? StringInterpolation;
			UsingDeclarations = sect.Attribute<bool?>(nameof(UsingDeclarations)) ?? UsingDeclarations;
			UseDebugSymbols = sect.Attribute<bool?>(nameof(UseDebugSymbols)) ?? UseDebugSymbols;
			ShowXmlDocumentation = sect.Attribute<bool?>(nameof(ShowXmlDocumentation)) ?? ShowXmlDocumentation;
			CleanUpOutput = sect.Attribute<bool?>(nameof(CleanUpOutput)) ?? CleanUpOutput;
			disableSave = false;
		}
		readonly bool disableSave;

		protected override void OnModified() {
			if (disableSave)
				return;

			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(AnonymousMethods), AnonymousMethods);
			sect.Attribute(nameof(ExpressionTrees), ExpressionTrees);
			sect.Attribute(nameof(YieldReturn), YieldReturn);
			sect.Attribute(nameof(AsyncAwait), AsyncAwait);
			sect.Attribute(nameof(QueryExpressions), QueryExpressions);
			sect.Attribute(nameof(SwitchExpressions), SwitchExpressions);
			sect.Attribute(nameof(StringInterpolation), StringInterpolation);
			sect.Attribute(nameof(UsingDeclarations), UsingDeclarations);
			sect.Attribute(nameof(UseDebugSymbols), UseDebugSymbols);
			sect.Attribute(nameof(ShowXmlDocumentation), ShowXmlDocumentation);
			sect.Attribute(nameof(CleanUpOutput), CleanUpOutput);
		}
	}
}
