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
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Windows;
using dnSpy.AsmEditor.Properties;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

// The Undo History window (Edit > Undo History): every edit that can be undone or redone, the current state marked;
// double-click an edit to undo or redo to it.
namespace dnSpy.AsmEditor.UndoRedo {
	[Export(typeof(IToolWindowContentProvider))]
	sealed class UndoHistoryToolWindowContentProvider : IToolWindowContentProvider {
		readonly Lazy<UndoHistoryVM> vm;
		UndoHistoryToolWindowContent Content => content ??= new UndoHistoryToolWindowContent(vm.Value);
		UndoHistoryToolWindowContent? content;

		[ImportingConstructor]
		UndoHistoryToolWindowContentProvider(Lazy<UndoHistoryVM> vm) => this.vm = vm;

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(UndoHistoryToolWindowContent.THE_GUID, UndoHistoryToolWindowContent.DEFAULT_LOCATION, 3000, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == UndoHistoryToolWindowContent.THE_GUID ? Content : null;
	}

	sealed class UndoHistoryToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("6E2B9C4D-7A13-4F85-B0D6-3C8E1F92A457");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => dnSpy_AsmEditor_Resources.UndoHistory_Window;
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.ListView;
		public override FrameworkElement? ZoomElement => control;

		readonly UndoHistoryVM vm;
		readonly UndoHistoryControl control;

		public UndoHistoryToolWindowContent(UndoHistoryVM vm) {
			this.vm = vm;
			control = new UndoHistoryControl { DataContext = vm };
		}

		public override void OnVisibilityChanged(ToolWindowContentVisibilityEvent visEvent) {
			switch (visEvent) {
			case ToolWindowContentVisibilityEvent.Added:
			case ToolWindowContentVisibilityEvent.Visible:
				vm.IsVisible = true;
				break;
			case ToolWindowContentVisibilityEvent.Removed:
			case ToolWindowContentVisibilityEvent.Hidden:
				vm.IsVisible = false;
				break;
			}
		}
	}

	/// <summary>
	/// A row: the state before any edit, or the state after an edit
	/// </summary>
	sealed class UndoHistoryRowVM {
		/// <summary>The number of edits applied in this state (<see cref="IUndoCommandService.GoTo(int)"/>)</summary>
		public int UndoCount { get; }
		public string Number { get; }
		public string Description { get; }
		public string Documents { get; }
		public string Time { get; }
		/// <summary>The current state</summary>
		public bool IsCurrent { get; }
		/// <summary>An edit that was undone (it can be redone)</summary>
		public bool IsUndone { get; }
		public string Marker => IsCurrent ? "\u25B6" : string.Empty;

		public UndoHistoryRowVM(int undoCount, UndoHistoryItem? item, bool isCurrent, bool isUndone) {
			UndoCount = undoCount;
			Number = undoCount == 0 ? string.Empty : undoCount.ToString(CultureInfo.CurrentCulture);
			Description = item?.Description ?? dnSpy_AsmEditor_Resources.UndoHistory_Original;
			Documents = item?.Documents ?? string.Empty;
			Time = item?.Time.ToString("T", CultureInfo.CurrentCulture) ?? string.Empty;
			IsCurrent = isCurrent;
			IsUndone = isUndone;
		}
	}

	[Export(typeof(UndoHistoryVM))]
	sealed class UndoHistoryVM : ViewModelBase {
		readonly IUndoCommandService undoCommandService;

		public ObservableCollection<UndoHistoryRowVM> Rows { get; } = new ObservableCollection<UndoHistoryRowVM>();

		public UndoHistoryRowVM? SelectedRow {
			get => selectedRow;
			set {
				if (selectedRow != value) {
					selectedRow = value;
					OnPropertyChanged(nameof(SelectedRow));
				}
			}
		}
		UndoHistoryRowVM? selectedRow;

		public bool IsVisible {
			get => isVisible;
			set {
				if (isVisible != value) {
					isVisible = value;
					if (isVisible && isDirty)
						Refresh();
				}
			}
		}
		bool isVisible;
		bool isDirty = true;
		bool isMoving;

		[ImportingConstructor]
		UndoHistoryVM(IUndoCommandService undoCommandService) {
			this.undoCommandService = undoCommandService;
			undoCommandService.OnEvent += UndoCommandService_OnEvent;
		}

		void UndoCommandService_OnEvent(object? sender, UndoCommandServiceEventArgs e) {
			switch (e.Type) {
			case UndoCommandServiceEventType.Add:
			case UndoCommandServiceEventType.Undo:
			case UndoCommandServiceEventType.Redo:
			case UndoCommandServiceEventType.ClearUndo:
			case UndoCommandServiceEventType.ClearRedo:
				isDirty = true;
				if (isVisible && !isMoving)
					Refresh();
				break;
			}
		}

		void Refresh() {
			isDirty = false;
			var rows = CreateRows(undoCommandService.UndoHistory, undoCommandService.RedoHistory);
			Rows.Clear();
			UndoHistoryRowVM? current = null;
			foreach (var row in rows) {
				Rows.Add(row);
				if (row.IsCurrent)
					current = row;
			}
			SelectedRow = current;
		}

		/// <summary>
		/// The rows: before the first edit, the applied edits (the last one is current), then the undone edits
		/// </summary>
		internal static List<UndoHistoryRowVM> CreateRows(IReadOnlyList<UndoHistoryItem> undo, IReadOnlyList<UndoHistoryItem> redo) {
			var rows = new List<UndoHistoryRowVM>(undo.Count + redo.Count + 1);
			rows.Add(new UndoHistoryRowVM(0, null, undo.Count == 0, false));
			for (int i = 0; i < undo.Count; i++)
				rows.Add(new UndoHistoryRowVM(i + 1, undo[i], i == undo.Count - 1, false));
			for (int i = 0; i < redo.Count; i++)
				rows.Add(new UndoHistoryRowVM(undo.Count + i + 1, redo[i], false, true));
			return rows;
		}

		/// <summary>
		/// Undoes or redoes to the state of a row
		/// </summary>
		public void GoTo(UndoHistoryRowVM? row) {
			if (row is null || row.IsCurrent)
				return;
			isMoving = true;
			try {
				undoCommandService.GoTo(row.UndoCount);
			}
			finally {
				isMoving = false;
				Refresh();
			}
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID, Header = "res:UndoHistoryCommand", Group = MenuConstants.GROUP_APP_MENU_EDIT_UNDO, Order = 20)]
	sealed class UndoHistoryWindowCommand : MenuItemBase {
		readonly IDsToolWindowService toolWindowService;

		[ImportingConstructor]
		UndoHistoryWindowCommand(IDsToolWindowService toolWindowService) => this.toolWindowService = toolWindowService;

		public override void Execute(IMenuItemContext context) => toolWindowService.Show(UndoHistoryToolWindowContent.THE_GUID);
	}
}
