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
using System.Text;

namespace dnSpy.MainApp.CommandPalette {
	/// <summary>
	/// Fuzzy matching for the command palette. Each word of the query must be found in the text, either as
	/// a substring or as a subsequence (eg. "fs" matches "Full Screen"). Doesn't use WPF so it can be tested headlessly.
	/// </summary>
	static class CommandPaletteMatcher {
		public const string PathSeparator = " > ";

		/// <summary>
		/// Splits the query into lower case words
		/// </summary>
		public static string[] GetWords(string? query) {
			if (query is null)
				return Array.Empty<string>();
			var words = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < words.Length; i++)
				words[i] = words[i].ToLowerInvariant();
			return words;
		}

		/// <summary>
		/// Returns the score (higher is better) or -1 if <paramref name="text"/> doesn't match all words.
		/// No words match everything with score 0.
		/// </summary>
		public static int GetScore(string[] words, string text) {
			if (words.Length == 0)
				return 0;
			var lower = text.ToLowerInvariant();
			int total = 0;
			foreach (var word in words) {
				int score = GetWordScore(word, text, lower);
				if (score < 0)
					return -1;
				total += score;
			}
			// Prefer shorter texts when the scores are equal
			return total * 4 - Math.Min(lower.Length, 200) / 50;
		}

		static bool IsWordStart(string text, int index) {
			if (index == 0)
				return true;
			char p = text[index - 1], c = text[index];
			if (!char.IsLetterOrDigit(p))
				return char.IsLetterOrDigit(c);
			return char.IsUpper(c) && !char.IsUpper(p);
		}

		static int GetWordScore(string word, string text, string lower) {
			int best = -1;
			// Substrings: the best one is at a word start, the earlier the better
			for (int index = lower.IndexOf(word, StringComparison.Ordinal); index >= 0; index = lower.IndexOf(word, index + 1, StringComparison.Ordinal)) {
				int score = 100 + word.Length * 10 + (IsWordStart(text, index) ? 100 : 0) - Math.Min(index, 50);
				if (score > best)
					best = score;
			}
			if (best >= 0)
				return best;

			// Subsequence: prefer word starts ("fs" -> "Full Screen")
			int pos = 0, subScore = 0;
			foreach (char c in word) {
				int found = -1;
				for (int i = pos; i < lower.Length; i++) {
					if (lower[i] == c && IsWordStart(text, i)) {
						found = i;
						break;
					}
				}
				bool wordStart = found >= 0;
				if (found < 0)
					found = lower.IndexOf(c, pos);
				if (found < 0)
					return -1;
				subScore += wordStart ? 10 : 1;
				pos = found + 1;
			}
			return subScore;
		}

		/// <summary>
		/// Removes the access key underscores of a menu header ("_File" -> "File", "A__B" -> "A_B")
		/// </summary>
		public static string RemoveAccessKeys(string? header) {
			if (header is null)
				return string.Empty;
			if (header.IndexOf('_') < 0)
				return header;
			var sb = new StringBuilder(header.Length);
			for (int i = 0; i < header.Length; i++) {
				char c = header[i];
				if (c == '_') {
					if (i + 1 < header.Length && header[i + 1] == '_') {
						sb.Append('_');
						i++;
					}
					continue;
				}
				sb.Append(c);
			}
			return sb.ToString();
		}

		/// <summary>
		/// Returns the indexes of the items that match, best first (same score: original order)
		/// </summary>
		public static List<int> Filter(IList<string> texts, string? query) {
			var words = GetWords(query);
			var result = new List<(int index, int score)>();
			for (int i = 0; i < texts.Count; i++) {
				int score = GetScore(words, texts[i]);
				if (score >= 0)
					result.Add((i, score));
			}
			result.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : a.index.CompareTo(b.index));
			var list = new List<int>(result.Count);
			foreach (var r in result)
				list.Add(r.index);
			return list;
		}
	}
}
