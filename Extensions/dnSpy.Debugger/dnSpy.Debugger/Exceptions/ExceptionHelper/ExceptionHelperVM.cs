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
using System.Text;
using System.Windows;
using System.Windows.Input;
using dnSpy.Contracts.MVVM;

namespace dnSpy.Debugger.Exceptions.ExceptionHelper {
	sealed class ExceptionHelperVM : ViewModelBase {
		public string Title { get; }
		public string ExceptionType { get; }
		public string Message { get; }
		public string Details { get; }
		public string ProcessName { get; }
		public bool CanBreakWhenThrown { get; }

		public bool BreakWhenThrown {
			get => breakWhenThrown;
			set {
				if (breakWhenThrown != value) {
					breakWhenThrown = value;
					OnPropertyChanged(nameof(BreakWhenThrown));
				}
			}
		}
		bool breakWhenThrown;

		public ICommand CopyDetailsCommand => new RelayCommand(a => CopyDetails());

		public ExceptionHelperVM(string title, string exceptionType, string message, string details, string processName, bool canBreakWhenThrown, bool breakWhenThrown) {
			Title = title;
			ExceptionType = exceptionType;
			Message = message;
			Details = details;
			ProcessName = processName;
			CanBreakWhenThrown = canBreakWhenThrown;
			this.breakWhenThrown = breakWhenThrown;
		}

		internal string GetText() {
			var sb = new StringBuilder();
			sb.AppendLine(Title);
			sb.AppendLine(ExceptionType);
			sb.AppendLine(Message);
			sb.AppendLine(ProcessName);
			sb.AppendLine();
			sb.Append(Details);
			return sb.ToString();
		}

		void CopyDetails() {
			try {
				Clipboard.SetText(GetText());
			}
			catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException || ex is System.Threading.ThreadStateException) {
			}
		}
	}
}
