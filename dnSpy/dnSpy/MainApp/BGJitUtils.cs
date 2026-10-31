/*
    Copyright (C) 2014-2019 de4dot@gmail.com
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
using System.IO;
using dnSpy.Contracts.App;

namespace dnSpy.MainApp {
	static class BGJitUtils {
		public static string GetFolder() {
#if NET
			const string frameworkDir = "net";
#elif NETFRAMEWORK
			const string frameworkDir = "netframework";
#else
#error Unknown framework
#endif
			return Path.Combine(AppDirectories.LocalDataDirectory, "Startup" + (IntPtr.Size * 8).ToString(), frameworkDir);
		}
	}
}
