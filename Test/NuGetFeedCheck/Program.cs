/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All NuGet feed checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Documents.Containers;

int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
string Show(IEnumerable<NuGetSource> sources) => string.Join(" | ", sources.Select(a => $"{a.Name} {a.Kind} {a.Location}"));

var work = Path.Combine(Path.GetTempPath(), "dnspy-nugetfeedcheck-" + Guid.NewGuid().ToString("N").Substring(0, 8));
Directory.CreateDirectory(work);
var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy", "NuGet");
var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
var cachedFiles = new List<string>();
try {
	// Nothing comes from the real global packages folder
	Environment.SetEnvironmentVariable("NUGET_PACKAGES", Path.Combine(work, "global-packages"));

	// ---- NuGet.config files
	string WriteConfig(string dir, string xml) {
		Directory.CreateDirectory(dir);
		var file = Path.Combine(dir, "NuGet.Config");
		File.WriteAllText(file, xml);
		return file;
	}
	var machine = WriteConfig(Path.Combine(work, "machine"), """
		<?xml version="1.0" encoding="utf-8"?>
		<configuration>
		  <packageSources>
		    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
		    <add key="Machine" value="https://machine.example/api/v2" />
		    <add key="Company" value="https://old.example/v3/index.json" />
		  </packageSources>
		</configuration>
		""");
	Environment.SetEnvironmentVariable("NUGETFEEDCHECK_DIR", Path.Combine(work, "envdir"));
	var user = WriteConfig(Path.Combine(work, "user"), """
		<?xml version="1.0" encoding="utf-8"?>
		<configuration>
		  <packageSources>
		    <add key="company" value="https://new.example/nuget/" protocolVersion="3" />
		    <remove key="Machine" />
		    <add key="Local" value="feed" />
		    <add key="Env" value="%NUGETFEEDCHECK_DIR%\packages" />
		    <add key="Ftp" value="ftp://ftp.example/feed" />
		  </packageSources>
		  <disabledPackageSources>
		    <add key="nuget.org" value="true" />
		    <add key="Local" value="true" />
		    <add key="Local" value="false" />
		  </disabledPackageSources>
		  <packageSourceCredentials>
		    <company><add key="Username" value="ignored" /></company>
		  </packageSourceCredentials>
		</configuration>
		""");
	var got = Show(NuGetSources.Read(new[] { machine, user }));
	var expected = $"company V3 https://new.example/nuget/ | Local Folder {Path.Combine(work, "user", "feed")} | Env Folder {Path.Combine(work, "envdir", "packages")}";
	Check(got == expected, $"machine + user config: got {got}, expected {expected}");

	var cleared = WriteConfig(Path.Combine(work, "cleared"), """
		<configuration>
		  <packageSources>
		    <clear />
		    <add key="Private" value="https://private.example/nuget" />
		    <add key="Abs" value="file:///C:/Feeds/abs" />
		  </packageSources>
		</configuration>
		""");
	got = Show(NuGetSources.Read(new[] { machine, cleared }));
	expected = $"Private V2 https://private.example/nuget | Abs Folder C:\\Feeds\\abs";
	Check(got == expected, $"<clear />: got {got}, expected {expected}");

	got = Show(NuGetSources.Read(new[] { Path.Combine(work, "missing", "NuGet.Config") }));
	expected = $"nuget.org V3 {NuGetSources.NuGetOrgUrl}";
	Check(got == expected, $"no config file: got {got}, expected {expected}");

	var broken = WriteConfig(Path.Combine(work, "broken"), "<configuration><packageSources>");
	var allDisabled = WriteConfig(Path.Combine(work, "disabled"), """
		<configuration>
		  <packageSources><clear /><add key="A" value="https://a.example/v3/index.json" /></packageSources>
		  <disabledPackageSources><add key="a" value="True" /></disabledPackageSources>
		</configuration>
		""");
	got = Show(NuGetSources.Read(new[] { broken, allDisabled }));
	Check(got == expected, $"broken file + all sources disabled: got {got}, expected {expected}");
	Check(NuGetSources.GetDefaultConfigFiles().LastOrDefault()?.EndsWith(Path.Combine("NuGet", "NuGet.Config"), StringComparison.OrdinalIgnoreCase) == true,
		"default config files end with the user's %APPDATA%\\NuGet\\NuGet.Config");

	// ---- Versions
	Check(NuGetClient.NormalizeVersion("1.0") == "1.0.0" && NuGetClient.NormalizeVersion("1.2.3.0+abc") == "1.2.3" && NuGetClient.NormalizeVersion("01.2-Beta") == "1.2.0-Beta",
		"NormalizeVersion");
	var sorted = new[] { "1.9.0", "1.10.0", "1.10.0-beta", "1.0", "2.0.0-alpha", "1.10.0-alpha" }.OrderByDescending(a => a, NuGetClient.VersionComparer.Instance).ToArray();
	Check(string.Join(",", sorted) == "2.0.0-alpha,1.10.0,1.10.0-beta,1.10.0-alpha,1.9.0,1.0", "version order: " + string.Join(",", sorted));

	// ---- Packages and fake feeds
	byte[] MakePackage(string id, string version, string? description = null) {
		using var ms = new MemoryStream();
		using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) {
			using (var w = new StreamWriter(zip.CreateEntry(id + ".nuspec").Open()))
				w.Write($"""
					<?xml version="1.0" encoding="utf-8"?>
					<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
					  <metadata><id>{id}</id><version>{version}</version><authors>Check Author</authors><description>{description ?? id + " " + version}</description></metadata>
					</package>
					""");
			using (var w = new StreamWriter(zip.CreateEntry("lib/net8.0/" + id + ".dll").Open()))
				w.Write("not a real assembly");
		}
		return ms.ToArray();
	}

	var flat = Path.Combine(work, "flat");
	Directory.CreateDirectory(flat);
	File.WriteAllBytes(Path.Combine(flat, "Check.Local.1.0.0.nupkg"), MakePackage("Check.Local", "1.0.0"));
	File.WriteAllBytes(Path.Combine(flat, "Check.Local.1.10.0.nupkg"), MakePackage("Check.Local", "1.10.0", "newest local"));
	File.WriteAllBytes(Path.Combine(flat, "Check.Local.1.9.0-beta.nupkg"), MakePackage("Check.Local", "1.9.0-beta"));
	File.WriteAllBytes(Path.Combine(flat, "renamed.nupkg"), MakePackage("Check.Renamed." + unique, "1.0"));
	File.WriteAllBytes(Path.Combine(flat, "Check.Shared.9.0.0.nupkg"), MakePackage("Check.Shared", "9.0.0"));
	File.WriteAllText(Path.Combine(flat, "notapackage.nupkg"), "garbage");
	File.WriteAllBytes(Path.Combine(flat, "Other.1.0.0.nupkg"), MakePackage("Other", "1.0.0"));
	var tree = Path.Combine(work, "tree");
	var treeFile = Path.Combine(tree, "check.tree", "2.0.0", "check.tree.2.0.0.nupkg");
	Directory.CreateDirectory(Path.GetDirectoryName(treeFile)!);
	File.WriteAllBytes(treeFile, MakePackage("Check.Tree", "2.0.0"));

	var remoteId = "Check.Remote." + unique;
	var remotePackage = MakePackage(remoteId, "1.0.0");
	var v2Id = "Check.V2." + unique;
	var v2Package = MakePackage(v2Id, "3.1.0");
	var handler = new FakeHandler();
	handler.Add("https://private.example/v3/index.json", HttpStatusCode.Unauthorized, "");
	handler.Add("https://feed.example/v3/index.json", HttpStatusCode.OK, """
		{ "version": "3.0.0", "resources": [
		  { "@id": "https://feed.example/query", "@type": "SearchQueryService/3.5.0" },
		  { "@id": "https://feed.example/flat/", "@type": "PackageBaseAddress/3.0.0" } ] }
		""");
	handler.Add("https://feed.example/query?q=check&take=50&semVerLevel=2.0.0&prerelease=false", HttpStatusCode.OK, $$"""
		{ "totalHits": 2, "data": [
		  { "id": "{{remoteId}}", "version": "1.0.0", "description": "remote", "authors": ["A", "B"], "totalDownloads": 1234, "versions": [ { "version": "0.9.0" }, { "version": "1.0.0" } ] },
		  { "id": "Check.Shared", "version": "1.0.0", "description": "from the feed", "authors": "C", "versions": [ { "version": "1.0.0" } ] } ] }
		""");
	handler.Add($"https://feed.example/flat/{remoteId.ToLowerInvariant()}/1.0.0/{remoteId.ToLowerInvariant()}.1.0.0.nupkg", HttpStatusCode.OK, remotePackage);
	handler.Add($"https://v2.example/api/v2/package/{v2Id}/3.1.0", HttpStatusCode.OK, v2Package);
	handler.Add("https://broken.example/v3/index.json", HttpStatusCode.OK, "{ not json");
	NuGetClient.HttpClient = new HttpClient(handler);

	var privateFeed = new NuGetSource("Private", "https://private.example/v3/index.json", NuGetSourceKind.V3);
	var feed = new NuGetSource("Feed", "https://feed.example/v3/index.json", NuGetSourceKind.V3);
	var v2 = new NuGetSource("V2", "https://v2.example/api/v2/", NuGetSourceKind.V2);
	var flatSource = new NuGetSource("Flat", flat, NuGetSourceKind.Folder);
	var treeSource = new NuGetSource("Tree", tree, NuGetSourceKind.Folder);
	var missingFolder = new NuGetSource("Missing", Path.Combine(work, "does-not-exist"), NuGetSourceKind.Folder);

	// ---- Search
	string ShowResults(List<NuGetSearchResult> results) => string.Join(" | ", results.Select(a => $"{a.Id} {a.Version} [{string.Join(",", a.Versions)}] {a.Description}"));
	NuGetClient.Sources = new List<NuGetSource> { privateFeed, feed, v2, flatSource, treeSource, missingFolder };
	got = ShowResults(await NuGetClient.SearchAsync("check", false, CancellationToken.None));
	expected = $"{remoteId} 1.0.0 [1.0.0,0.9.0] remote | Check.Shared 1.0.0 [1.0.0] from the feed | Check.Local 1.10.0 [1.10.0,1.0.0] newest local | Check.Renamed.{unique} 1.0 [1.0] Check.Renamed.{unique} 1.0 | Check.Tree 2.0.0 [2.0.0] Check.Tree 2.0.0";
	Check(got == expected, $"search (a failing private feed, a feed, folders): got {got}, expected {expected}");
	NuGetClient.Sources = new List<NuGetSource> { flatSource };
	got = ShowResults(await NuGetClient.SearchAsync("LOCAL", true, CancellationToken.None));
	expected = "Check.Local 1.10.0 [1.10.0,1.9.0-beta,1.0.0] newest local";
	Check(got == expected, $"search a folder with prereleases: got {got}, expected {expected}");
	got = ShowResults(await NuGetClient.SearchAsync("", false, CancellationToken.None));
	Check(got.Contains("Other 1.0.0") && got.Contains("Check.Shared 9.0.0"), "empty search lists the folder's packages: " + got);

	async Task<string> ErrorOf(Func<Task> action) {
		try {
			await action();
			return "no exception";
		}
		catch (Exception ex) {
			return ex.GetType().Name;
		}
	}
	NuGetClient.Sources = new List<NuGetSource> { privateFeed };
	got = await ErrorOf(() => NuGetClient.SearchAsync("check", false, CancellationToken.None));
	Check(got == nameof(HttpRequestException), "search, only a failing feed: " + got);
	NuGetClient.Sources = new List<NuGetSource> { new NuGetSource("Broken", "https://broken.example/v3/index.json", NuGetSourceKind.V3) };
	got = await ErrorOf(() => NuGetClient.SearchAsync("check", false, CancellationToken.None));
	Check(got == nameof(HttpRequestException), "search, only a feed with a broken service index (the dialog shows HttpRequestException): " + got);
	NuGetClient.Sources = new List<NuGetSource> { v2 };
	Check((await NuGetClient.SearchAsync("check", false, CancellationToken.None)).Count == 0, "search, only a V2 feed (can't be searched): no results");

	// ---- Downloads and folders
	NuGetClient.Sources = new List<NuGetSource> { privateFeed, missingFolder, v2, feed, flatSource, treeSource };
	var file = await NuGetClient.GetPackageAsync(remoteId, "1.0", CancellationToken.None);
	if (file is not null)
		cachedFiles.Add(file);
	Check(file == Path.Combine(cacheDir, remoteId.ToLowerInvariant() + ".1.0.0.nupkg") && File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(remotePackage),
		"download from a V3 feed after a failing private feed and a V2 feed without it: " + file);
	Check(handler.Requests.Contains($"https://v2.example/api/v2/package/{remoteId}/1.0.0"), "the V2 feed was asked first (sources are tried in order)");
	file = await NuGetClient.GetPackageAsync(v2Id, "3.1.0", CancellationToken.None);
	if (file is not null)
		cachedFiles.Add(file);
	Check(file is not null && File.ReadAllBytes(file).SequenceEqual(v2Package), "download from a V2 feed: " + file);
	Check(await NuGetClient.GetPackageAsync("check.tree", "2.0", CancellationToken.None) == treeFile, "folder with id/version folders");
	Check(await NuGetClient.GetPackageAsync("Check.Local", "1.10.0", CancellationToken.None) == Path.Combine(flat, "Check.Local.1.10.0.nupkg"), "flat folder");
	Check(await NuGetClient.GetPackageAsync("check.renamed." + unique, "1.0.0", CancellationToken.None) == Path.Combine(flat, "renamed.nupkg"), "flat folder, other file name (matched by its .nuspec)");
	NuGetClient.Sources = new List<NuGetSource> { feed, flatSource };
	Check(await NuGetClient.GetPackageAsync("Not.There." + unique, "1.0.0", CancellationToken.None) is null, "not in any source: null");
	NuGetClient.Sources = new List<NuGetSource> { privateFeed, flatSource };
	got = await ErrorOf(() => NuGetClient.GetPackageAsync("Not.There." + unique, "1.0.0", CancellationToken.None));
	Check(got == nameof(HttpRequestException), "not found and a feed failed: " + got);
	NuGetClient.Sources = new List<NuGetSource> { privateFeed, flatSource };
	Check(await NuGetClient.GetPackageAsync("Check.Local", "1.0.0", CancellationToken.None) == Path.Combine(flat, "Check.Local.1.0.0.nupkg"), "found after a failing feed: no error");
}
finally {
	foreach (var f in cachedFiles) {
		try { File.Delete(f); } catch (IOException) { }
	}
	try { Directory.Delete(work, true); } catch (IOException) { }
}

if (fails == 0)
	Console.WriteLine("All NuGet feed checks passed");
return fails == 0 ? 0 : 1;

sealed class FakeHandler : HttpMessageHandler {
	readonly Dictionary<string, (HttpStatusCode status, byte[] content)> responses = new Dictionary<string, (HttpStatusCode, byte[])>(StringComparer.Ordinal);
	public List<string> Requests { get; } = new List<string>();
	public void Add(string url, HttpStatusCode status, string content) => responses[url] = (status, Encoding.UTF8.GetBytes(content));
	public void Add(string url, HttpStatusCode status, byte[] content) => responses[url] = (status, content);
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
		var url = request.RequestUri!.ToString();
		lock (Requests)
			Requests.Add(url);
		var response = responses.TryGetValue(url, out var r) ?
			new HttpResponseMessage(r.status) { Content = new ByteArrayContent(r.content) } :
			new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent(Array.Empty<byte>()) };
		return Task.FromResult(response);
	}
}
