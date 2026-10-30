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
using dnSpy.Contracts.Images;
using dnSpy.Contracts.MVVM;

namespace dnSpy.MainApp.CommandPalette {
	/// <summary>
	/// A command of the main menu
	/// </summary>
	sealed class CommandPaletteItem {
		/// <summary>
		/// The menu path, eg. "View > Full Screen"
		/// </summary>
		public string Text { get; }
		public string InputGestureText { get; }
		public ImageReference ImageReference { get; }
		public bool HasImage { get; }
		public bool IsChecked { get; }
		readonly Action execute;

		public CommandPaletteItem(string text, string? inputGestureText, ImageReference? imageReference, bool isChecked, Action execute) {
			Text = text;
			InputGestureText = inputGestureText ?? string.Empty;
			ImageReference = imageReference ?? default;
			HasImage = imageReference is not null;
			IsChecked = isChecked;
			this.execute = execute;
		}

		public void Execute() => execute();
		public override string ToString() => Text;
	}

	sealed class CommandPaletteVM : ViewModelBase {
		public ObservableCollection<CommandPaletteItem> Items { get; } = new ObservableCollection<CommandPaletteItem>();

		public string SearchText {
			get => searchText;
			set {
				if (searchText != value) {
					searchText = value;
					OnPropertyChanged(nameof(SearchText));
					Refilter();
				}
			}
		}
		string searchText = string.Empty;

		public CommandPaletteItem? SelectedItem {
			get => selectedItem;
			set {
				if (selectedItem != value) {
					selectedItem = value;
					OnPropertyChanged(nameof(SelectedItem));
				}
			}
		}
		CommandPaletteItem? selectedItem;

		public bool HasNoItems => Items.Count == 0;

		/// <summary>
		/// The item to execute after the dialog was closed
		/// </summary>
		public CommandPaletteItem? ItemToExecute { get; private set; }

		readonly List<CommandPaletteItem> allItems;
		readonly string[] allTexts;

		public CommandPaletteVM(List<CommandPaletteItem> allItems) {
			this.allItems = allItems;
			allTexts = new string[allItems.Count];
			for (int i = 0; i < allTexts.Length; i++)
				allTexts[i] = allItems[i].Text;
			Refilter();
		}

		void Refilter() {
			Items.Clear();
			foreach (var index in CommandPaletteMatcher.Filter(allTexts, searchText))
				Items.Add(allItems[index]);
			SelectedItem = Items.Count == 0 ? null : Items[0];
			OnPropertyChanged(nameof(HasNoItems));
		}

		public void MoveSelection(int delta) {
			if (Items.Count == 0)
				return;
			int index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
			index = Math.Max(0, Math.Min(Items.Count - 1, index + delta));
			SelectedItem = Items[index];
		}

		public bool Accept(CommandPaletteItem? item) {
			item ??= SelectedItem;
			if (item is null)
				return false;
			ItemToExecute = item;
			return true;
		}
	}
}
