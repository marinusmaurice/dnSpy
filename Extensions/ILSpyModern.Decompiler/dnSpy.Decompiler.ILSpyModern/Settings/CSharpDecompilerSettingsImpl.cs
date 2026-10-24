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
			AutomaticProperties = sect.Attribute<bool?>(nameof(AutomaticProperties)) ?? AutomaticProperties;
			AutomaticEvents = sect.Attribute<bool?>(nameof(AutomaticEvents)) ?? AutomaticEvents;
			UsingStatement = sect.Attribute<bool?>(nameof(UsingStatement)) ?? UsingStatement;
			ForEachStatement = sect.Attribute<bool?>(nameof(ForEachStatement)) ?? ForEachStatement;
			LockStatement = sect.Attribute<bool?>(nameof(LockStatement)) ?? LockStatement;
			ObjectOrCollectionInitializers = sect.Attribute<bool?>(nameof(ObjectOrCollectionInitializers)) ?? ObjectOrCollectionInitializers;
			IntroduceIncrementAndDecrement = sect.Attribute<bool?>(nameof(IntroduceIncrementAndDecrement)) ?? IntroduceIncrementAndDecrement;
			MakeAssignmentExpressions = sect.Attribute<bool?>(nameof(MakeAssignmentExpressions)) ?? MakeAssignmentExpressions;
			SortCustomAttributes = sect.Attribute<bool?>(nameof(SortCustomAttributes)) ?? SortCustomAttributes;
			MemberOrder = sect.Attribute<string>(nameof(MemberOrder)) ?? MemberOrder;
			SortMembers = sect.Attribute<bool?>(nameof(SortMembers)) ?? SortMembers;
			UseSourceCodeOrder = sect.Attribute<bool?>(nameof(UseSourceCodeOrder)) ?? UseSourceCodeOrder;
			UsingDeclarations = sect.Attribute<bool?>(nameof(UsingDeclarations)) ?? UsingDeclarations;
			UseDebugSymbols = sect.Attribute<bool?>(nameof(UseDebugSymbols)) ?? UseDebugSymbols;
			ShowXmlDocumentation = sect.Attribute<bool?>(nameof(ShowXmlDocumentation)) ?? ShowXmlDocumentation;
			ShowTokenAndRvaComments = sect.Attribute<bool?>(nameof(ShowTokenAndRvaComments)) ?? ShowTokenAndRvaComments;
			CleanUpOutput = sect.Attribute<bool?>(nameof(CleanUpOutput)) ?? CleanUpOutput;
			LanguageVersion = sect.Attribute<string>(nameof(LanguageVersion)) ?? LanguageVersion;
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
			sect.Attribute(nameof(AutomaticProperties), AutomaticProperties);
			sect.Attribute(nameof(AutomaticEvents), AutomaticEvents);
			sect.Attribute(nameof(UsingStatement), UsingStatement);
			sect.Attribute(nameof(ForEachStatement), ForEachStatement);
			sect.Attribute(nameof(LockStatement), LockStatement);
			sect.Attribute(nameof(ObjectOrCollectionInitializers), ObjectOrCollectionInitializers);
			sect.Attribute(nameof(IntroduceIncrementAndDecrement), IntroduceIncrementAndDecrement);
			sect.Attribute(nameof(MakeAssignmentExpressions), MakeAssignmentExpressions);
			sect.Attribute(nameof(SortCustomAttributes), SortCustomAttributes);
			sect.Attribute(nameof(MemberOrder), MemberOrder);
			sect.Attribute(nameof(SortMembers), SortMembers);
			sect.Attribute(nameof(UseSourceCodeOrder), UseSourceCodeOrder);
			sect.Attribute(nameof(UsingDeclarations), UsingDeclarations);
			sect.Attribute(nameof(UseDebugSymbols), UseDebugSymbols);
			sect.Attribute(nameof(ShowXmlDocumentation), ShowXmlDocumentation);
			sect.Attribute(nameof(ShowTokenAndRvaComments), ShowTokenAndRvaComments);
			sect.Attribute(nameof(CleanUpOutput), CleanUpOutput);
			sect.Attribute(nameof(LanguageVersion), LanguageVersion);
		}
	}
}
