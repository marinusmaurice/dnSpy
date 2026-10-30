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

		/// <summary>
		/// A query that starts with this searches the types and members of the loaded assemblies
		/// </summary>
		public const string SymbolPrefix = "#";

		/// <summary>
		/// The most types and members shown (there can be hundreds of thousands)
		/// </summary>
		public const int MaxSymbolResults = 200;

		/// <summary>
		/// true if the query searches types and members
		/// </summary>
		public bool IsSymbolSearch => searchText.StartsWith(SymbolPrefix, StringComparison.Ordinal);

		sealed class ItemList {
			public readonly List<CommandPaletteItem> Items;
			public readonly string[] Texts;
			public readonly string[] LowerTexts;

			public ItemList(List<CommandPaletteItem> items) {
				Items = items;
				Texts = new string[items.Count];
				LowerTexts = new string[items.Count];
				for (int i = 0; i < items.Count; i++) {
					Texts[i] = items[i].Text;
					LowerTexts[i] = Texts[i].ToLowerInvariant();
				}
			}
		}

		readonly ItemList commands;
		readonly Func<List<CommandPaletteItem>>? getSymbols;
		ItemList? symbols;

		public CommandPaletteVM(List<CommandPaletteItem> commands, Func<List<CommandPaletteItem>>? getSymbols = null) {
			this.commands = new ItemList(commands);
			this.getSymbols = getSymbols;
			Refilter();
		}

		void Refilter() {
			ItemList list;
			string query;
			int maxResults;
			if (IsSymbolSearch && getSymbols is not null) {
				// Collected the first time they're searched, the palette is reopened to see newly loaded assemblies
				symbols ??= new ItemList(getSymbols());
				list = symbols;
				query = searchText.Substring(SymbolPrefix.Length);
				maxResults = MaxSymbolResults;
			}
			else {
				list = commands;
				query = searchText;
				maxResults = int.MaxValue;
			}
			Items.Clear();
			foreach (var index in CommandPaletteMatcher.Filter(list.Texts, list.LowerTexts, query, maxResults))
				Items.Add(list.Items[index]);
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
