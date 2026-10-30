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
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack.TextEditor;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Contracts.Text.Editor;
using dnSpy.Contracts.ToolBars;
using dnSpy.Contracts.Utilities;
using dnSpy.Debugger.Properties;
using dnSpy.Debugger.UI;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;

namespace dnSpy.Debugger.History {
	/// <summary>
	/// Marks the viewed historical stop's statement in the code view (a "Backwards" glyph with the
	/// call return colors). Nothing is marked while the newest (live) stop is viewed, the live
	/// stop has the normal current statement marker.
	/// </summary>
	[Export(typeof(IDbgManagerStartListener))]
	sealed class DebugHistoryMarker : IDbgManagerStartListener {
		// Above the call return marker so it's visible if both are on the same statement
		const int HistoricalStatementZIndex = GlyphTextMarkerServiceZIndexes.ReturnStatement + 100;

		readonly UIDispatcher uiDispatcher;
		readonly Lazy<DbgHistoryRecorder> recorder;
		readonly Lazy<IGlyphTextMarkerService> glyphTextMarkerService;
		readonly Lazy<IClassificationTypeRegistryService> classificationTypeRegistryService;
		readonly Lazy<DbgCodeLocationGlyphTextMarkerLocationInfoProvider>[] locationInfoProviders;
		IClassificationType? classificationTypeHistoricalStatement;
		IGlyphTextMarker? historicalStatementMarker;
		DbgHistorySnapshot? markedSnapshot;

		[ImportingConstructor]
		DebugHistoryMarker(UIDispatcher uiDispatcher, Lazy<DbgHistoryRecorder> recorder, Lazy<IGlyphTextMarkerService> glyphTextMarkerService, Lazy<IClassificationTypeRegistryService> classificationTypeRegistryService, [ImportMany] IEnumerable<Lazy<DbgCodeLocationGlyphTextMarkerLocationInfoProvider>> locationInfoProviders) {
			this.uiDispatcher = uiDispatcher;
			this.recorder = recorder;
			this.glyphTextMarkerService = glyphTextMarkerService;
			this.classificationTypeRegistryService = classificationTypeRegistryService;
			this.locationInfoProviders = locationInfoProviders.ToArray();
		}

		void IDbgManagerStartListener.OnStart(DbgManager dbgManager) =>
			recorder.Value.History.Changed += DbgHistory_Changed;

		// random thread
		void DbgHistory_Changed(object? sender, EventArgs e) => uiDispatcher.UI(UpdateMarker_UI);

		void UpdateMarker_UI() {
			var history = recorder.Value.History;
			var snapshot = history.IsLive ? null : history.Current;
			if (snapshot == markedSnapshot)
				return;
			ClearMarker_UI();
			markedSnapshot = snapshot;
			if (snapshot?.TopFrame?.Location is not DbgCodeLocation location || location.IsClosed)
				return;
			var markerLocationInfo = GetTextMarkerLocationInfo(location);
			if (markerLocationInfo is null)
				return;
			classificationTypeHistoricalStatement ??= classificationTypeRegistryService.Value.GetClassificationType(ThemeClassificationTypeNames.CallReturn);
			historicalStatementMarker = glyphTextMarkerService.Value.AddMarker(
				markerLocationInfo,
				DsImages.Backwards,
				ThemeClassificationTypeNameKeys.CallReturnMarker,
				null,
				classificationTypeHistoricalStatement,
				HistoricalStatementZIndex,
				null,
				null,
				textViewFilter);
		}
		static readonly Func<ITextView, bool> textViewFilter = textView => textView.Roles.Contains(PredefinedTextViewRoles.Debuggable);

		void ClearMarker_UI() {
			markedSnapshot = null;
			if (historicalStatementMarker is not null) {
				glyphTextMarkerService.Value.Remove(historicalStatementMarker);
				historicalStatementMarker = null;
			}
		}

		GlyphTextMarkerLocationInfo? GetTextMarkerLocationInfo(DbgCodeLocation location) {
			foreach (var provider in locationInfoProviders) {
				var info = provider.Value.Create(location);
				if (info is not null)
					return info;
			}
			return null;
		}
	}

	static class DebugHistoryToolBarButtons {
		abstract class DebugHistoryToolBarButton : ToolBarButtonBase {
			// Prevents the debugger from being loaded since IsVisible will be called early
			[Export(typeof(IDbgManagerStartListener))]
			sealed class DbgManagerStartListener : IDbgManagerStartListener {
				public void OnStart(DbgManager dbgManager) => initd = true;
			}
			static bool initd;

			readonly Lazy<DbgUI.Debugger> debugger;
			readonly Lazy<DbgHistorySettings> dbgHistorySettings;
			protected readonly Lazy<DebugHistoryNavigator> navigator;

			protected DebugHistoryToolBarButton(Lazy<DbgUI.Debugger> debugger, Lazy<DbgHistorySettings> dbgHistorySettings, Lazy<DebugHistoryNavigator> navigator) {
				this.debugger = debugger;
				this.dbgHistorySettings = dbgHistorySettings;
				this.navigator = navigator;
			}

			// Only shown while debugging with recording on, or with a history to view
			public override bool IsVisible(IToolBarItemContext context) =>
				initd && debugger.Value.IsDebugging && (dbgHistorySettings.Value.IsEnabled || navigator.Value.History.Count > 0);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.Backwards, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_STEP, Order = 100)]
		sealed class StepBackToolBarButton : DebugHistoryToolBarButton {
			[ImportingConstructor]
			StepBackToolBarButton(Lazy<DbgUI.Debugger> debugger, Lazy<DbgHistorySettings> dbgHistorySettings, Lazy<DebugHistoryNavigator> navigator) : base(debugger, dbgHistorySettings, navigator) { }

			public override void Execute(IToolBarItemContext context) => navigator.Value.StepBack();
			public override bool IsEnabled(IToolBarItemContext context) => navigator.Value.CanStepBack;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(dnSpy_Debugger_Resources.DbgHistory_StepBack, dnSpy_Debugger_Resources.ShortCutKeyAltOpenBracket);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.Forwards, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_STEP, Order = 110)]
		sealed class StepForwardToolBarButton : DebugHistoryToolBarButton {
			[ImportingConstructor]
			StepForwardToolBarButton(Lazy<DbgUI.Debugger> debugger, Lazy<DbgHistorySettings> dbgHistorySettings, Lazy<DebugHistoryNavigator> navigator) : base(debugger, dbgHistorySettings, navigator) { }

			public override void Execute(IToolBarItemContext context) => navigator.Value.StepForward();
			public override bool IsEnabled(IToolBarItemContext context) => navigator.Value.CanStepForward;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(dnSpy_Debugger_Resources.DbgHistory_StepForward, dnSpy_Debugger_Resources.ShortCutKeyAltCloseBracket);
		}
	}
}
