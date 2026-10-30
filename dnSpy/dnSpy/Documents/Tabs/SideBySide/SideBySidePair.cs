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
using System.Diagnostics.CodeAnalysis;

namespace dnSpy.Documents.Tabs.SideBySide {
	/// <summary>
	/// The two tabs of a side-by-side view. When one of them shows something new, the other one follows, except while it's
	/// being synced (so the follow doesn't bounce back). Doesn't use WPF so it can be tested on its own.
	/// </summary>
	sealed class SideBySidePair<T> where T : class {
		T? first, second;
		int syncing;

		public bool IsActive => first is not null;

		public void Set(T a, T b) {
			if (a is null)
				throw new ArgumentNullException(nameof(a));
			if (b is null)
				throw new ArgumentNullException(nameof(b));
			if (ReferenceEquals(a, b))
				throw new ArgumentException("A tab can't be shown side by side with itself");
			first = a;
			second = b;
		}

		public void Clear() {
			first = null;
			second = null;
		}

		public bool Contains(T? tab) => tab is not null && (ReferenceEquals(tab, first) || ReferenceEquals(tab, second));

		public T? GetPartner(T? tab) {
			if (tab is null)
				return null;
			if (ReferenceEquals(tab, first))
				return second;
			if (ReferenceEquals(tab, second))
				return first;
			return null;
		}

		/// <summary>
		/// Called when a tab is closed: the side-by-side view ends when one of its tabs is closed
		/// </summary>
		public void Remove(T tab) {
			if (Contains(tab))
				Clear();
		}

		/// <summary>
		/// Gets the tab that must follow <paramref name="shown"/>, which showed something new
		/// </summary>
		public bool TryGetSyncTarget(T shown, [NotNullWhen(true)] out T? partner) {
			partner = syncing > 0 ? null : GetPartner(shown);
			return partner is not null;
		}

		/// <summary>
		/// Call while the partner is updated so its own "showed something new" isn't synced back
		/// </summary>
		public IDisposable BeginSync() {
			syncing++;
			return new SyncScope(this);
		}

		sealed class SyncScope : IDisposable {
			SideBySidePair<T>? owner;
			public SyncScope(SideBySidePair<T> owner) => this.owner = owner;
			public void Dispose() {
				if (owner is not null) {
					owner.syncing--;
					owner = null;
				}
			}
		}
	}
}
