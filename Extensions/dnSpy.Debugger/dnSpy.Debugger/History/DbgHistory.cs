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

namespace dnSpy.Debugger.History {
	/// <summary>
	/// Bounded history of the stops of a debug session (oldest stop first) and the stop that's
	/// being viewed. Thread safe: stops are added on the debugger thread and read on the UI thread.
	/// </summary>
	sealed class DbgHistory {
		public const int DefaultCapacity = 500;
		public const int MinCapacity = 1;
		public const int MaxCapacity = 100000;

		readonly object lockObj = new object();
		DbgHistorySnapshot[] items;
		// Index of the oldest snapshot in items
		int start;
		int count;
		long totalCount;
		DateTime? lastTime;
		// Viewed snapshot (0 = oldest), or -1 if the newest (live) stop is viewed
		int position = -1;

		/// <summary>
		/// Raised (outside the lock) after a stop was added or removed, the history was cleared, or the viewed stop changed
		/// </summary>
		public event EventHandler? Changed;

		/// <summary>
		/// Called (outside the lock) with each snapshot that's dropped or cleared, so its code locations can be closed
		/// </summary>
		public Action<DbgHistorySnapshot>? SnapshotRemoved { get; set; }

		public DbgHistory(int capacity = DefaultCapacity) => items = new DbgHistorySnapshot[ClampCapacity(capacity)];

		public static int ClampCapacity(int capacity) => Math.Max(MinCapacity, Math.Min(MaxCapacity, capacity));

		/// <summary>
		/// Max number of stops. Making it smaller drops the oldest stops.
		/// </summary>
		public int Capacity {
			get {
				lock (lockObj)
					return items.Length;
			}
			set {
				value = ClampCapacity(value);
				List<DbgHistorySnapshot>? removed = null;
				lock (lockObj) {
					if (value == items.Length)
						return;
					while (count > value)
						(removed ??= new List<DbgHistorySnapshot>()).Add(RemoveOldest_NoLock());
					if (position >= count - 1)
						position = -1;
					var newItems = new DbgHistorySnapshot[value];
					for (int i = 0; i < count; i++)
						newItems[i] = items[(start + i) % items.Length];
					items = newItems;
					start = 0;
				}
				OnRemoved(removed);
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}

		/// <summary>
		/// Number of stops in the history
		/// </summary>
		public int Count {
			get {
				lock (lockObj)
					return count;
			}
		}

		/// <summary>
		/// Number of stops added since the history was cleared, dropped stops included
		/// </summary>
		public long TotalCount {
			get {
				lock (lockObj)
					return totalCount;
			}
		}

		/// <summary>
		/// Number of stops dropped because the history was full
		/// </summary>
		public long DroppedCount {
			get {
				lock (lockObj)
					return totalCount - count;
			}
		}

		/// <summary>
		/// Gets a stop, 0 is the oldest one
		/// </summary>
		public DbgHistorySnapshot this[int index] {
			get {
				lock (lockObj) {
					if ((uint)index >= (uint)count)
						throw new ArgumentOutOfRangeException(nameof(index));
					return items[(start + index) % items.Length];
				}
			}
		}

		/// <summary>
		/// Gets all stops, oldest first
		/// </summary>
		public DbgHistorySnapshot[] GetSnapshots() {
			lock (lockObj) {
				var res = new DbgHistorySnapshot[count];
				for (int i = 0; i < count; i++)
					res[i] = items[(start + i) % items.Length];
				return res;
			}
		}

		/// <summary>
		/// Adds a stop and views it (returns to the live state)
		/// </summary>
		public void Add(DbgHistorySnapshot snapshot) {
			if (snapshot is null)
				throw new ArgumentNullException(nameof(snapshot));
			DbgHistorySnapshot? removed = null;
			lock (lockObj) {
				totalCount++;
				snapshot.Index = totalCount;
				if (lastTime is not null)
					snapshot.Elapsed = snapshot.Time - lastTime.Value;
				lastTime = snapshot.Time;
				if (count == items.Length)
					removed = RemoveOldest_NoLock();
				items[(start + count) % items.Length] = snapshot;
				count++;
				position = -1;
			}
			if (removed is not null)
				SnapshotRemoved?.Invoke(removed);
			Changed?.Invoke(this, EventArgs.Empty);
		}

		DbgHistorySnapshot RemoveOldest_NoLock() {
			var oldest = items[start];
			items[start] = null!;
			start = (start + 1) % items.Length;
			count--;
			if (position >= 0)
				position = Math.Max(0, position - 1);
			return oldest;
		}

		/// <summary>
		/// Removes all stops (a new debug session)
		/// </summary>
		public void Clear() {
			DbgHistorySnapshot[] removed;
			lock (lockObj) {
				if (count == 0 && totalCount == 0)
					return;
				removed = new DbgHistorySnapshot[count];
				for (int i = 0; i < count; i++)
					removed[i] = items[(start + i) % items.Length];
				Array.Clear(items, 0, items.Length);
				start = 0;
				count = 0;
				totalCount = 0;
				lastTime = null;
				position = -1;
			}
			OnRemoved(removed);
			Changed?.Invoke(this, EventArgs.Empty);
		}

		void OnRemoved(IList<DbgHistorySnapshot>? removed) {
			var callback = SnapshotRemoved;
			if (removed is null || callback is null)
				return;
			foreach (var snapshot in removed)
				callback(snapshot);
		}

		/// <summary>
		/// true if the newest stop is viewed
		/// </summary>
		public bool IsLive {
			get {
				lock (lockObj)
					return position < 0;
			}
		}

		/// <summary>
		/// Index (0 = oldest) of the viewed stop, or -1 if the history is empty
		/// </summary>
		public int CurrentIndex {
			get {
				lock (lockObj)
					return GetCurrentIndex_NoLock();
			}
		}

		int GetCurrentIndex_NoLock() => position >= 0 ? position : count - 1;

		/// <summary>
		/// Viewed stop: the newest one when live, or null if the history is empty
		/// </summary>
		public DbgHistorySnapshot? Current {
			get {
				lock (lockObj) {
					int index = GetCurrentIndex_NoLock();
					return index < 0 ? null : items[(start + index) % items.Length];
				}
			}
		}

		public bool CanMoveBack {
			get {
				lock (lockObj)
					return GetCurrentIndex_NoLock() > 0;
			}
		}

		public bool CanMoveForward {
			get {
				lock (lockObj)
					return position >= 0;
			}
		}

		/// <summary>
		/// Views the previous stop (Step Back)
		/// </summary>
		public bool MoveBack() {
			lock (lockObj) {
				int index = GetCurrentIndex_NoLock();
				if (index <= 0)
					return false;
				position = index - 1;
			}
			Changed?.Invoke(this, EventArgs.Empty);
			return true;
		}

		/// <summary>
		/// Views the next stop (Step Forward), the newest stop is live
		/// </summary>
		public bool MoveForward() {
			lock (lockObj) {
				if (position < 0)
					return false;
				position = position + 1 >= count - 1 ? -1 : position + 1;
			}
			Changed?.Invoke(this, EventArgs.Empty);
			return true;
		}

		/// <summary>
		/// Views a stop (0 = oldest), the newest stop is live
		/// </summary>
		public bool MoveTo(int index) {
			lock (lockObj) {
				if ((uint)index >= (uint)count)
					return false;
				int newPosition = index == count - 1 ? -1 : index;
				if (newPosition == position)
					return false;
				position = newPosition;
			}
			Changed?.Invoke(this, EventArgs.Empty);
			return true;
		}

		/// <summary>
		/// Views the newest stop
		/// </summary>
		public bool ReturnToLive() {
			lock (lockObj) {
				if (position < 0)
					return false;
				position = -1;
			}
			Changed?.Invoke(this, EventArgs.Empty);
			return true;
		}
	}
}
