using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RepositoryTools;

public static class RepositoryRoot
{
    public static string Find()
    {
        // File-based apps build outside the checkout, so locate the source from the
        // caller's working directory. Every documented command runs from the root.
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "gradle.properties")) &&
                File.Exists(Path.Combine(directory.FullName, "scripts", "rider-targets.json")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidDataException("Run from the repository directory, or provide --root <repository>.");
    }
}

public static class PropertiesFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator >= 0)
            {
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        return values;
    }
}

public static class ReleaseMetadata
{
    public static List<string> Validate(IReadOnlyDictionary<string, string> values, string root)
    {
        var errors = new List<string>();
        string Value(string key) => values.GetValueOrDefault(key, "");
        foreach (var key in new[] { "pluginId", "pluginName", "pluginVendor", "pluginVendorUrl", "pluginRepositoryUrl" })
        {
            var value = Value(key);
            if (string.IsNullOrWhiteSpace(value) || new[] { "unconfigured", "changeme", "example.", "todo" }
                    .Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Set {key} in gradle.properties");
            }
        }
        if (Value("pluginVendorEmail") is { Length: > 0 } email &&
            !Regex.IsMatch(email, @"\A[^\s@]+@[^\s@]+\.[^\s@]+\z", RegexOptions.CultureInvariant))
        {
            errors.Add("pluginVendorEmail must be a valid public support email address when provided");
        }
        foreach (var key in new[] { "pluginVendorUrl", "pluginRepositoryUrl" })
        {
            if (!Uri.TryCreate(Value(key), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrEmpty(url.Host) || url.UserInfo.Length != 0)
            {
                errors.Add($"{key} must be a real HTTPS URL without credentials");
            }
        }
        if (Uri.TryCreate(Value("pluginRepositoryUrl"), UriKind.Absolute, out var source) &&
            source.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            errors.Add("pluginRepositoryUrl must identify the actual source repository, not an account page");
        }
        var name = Value("pluginName");
        if (name.Length is < 1 or > 30 || new[] { "rider", "jetbrains", "intellij", "plugin" }
                .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Choose a name of at most 30 characters without JetBrains product names or Plugin");
        }
        var license = Path.Combine(root, "LICENSE");
        if (!File.Exists(license) || !File.ReadAllText(license).Contains("Permission is hereby granted", StringComparison.Ordinal))
        {
            errors.Add("Review the project license before release");
        }
        return errors;
    }
}

public sealed record RiderTarget(string Version, string Build, string Url, string Sha256);

public static class RiderSdk
{
    public static Dictionary<string, RiderTarget> ReadTargets(string path)
    {
        var targets = JsonSerializer.Deserialize<Dictionary<string, RiderTarget>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Missing Rider targets");
        if (targets.Count == 0)
        {
            throw new InvalidDataException("No Rider targets configured");
        }
        foreach (var (branch, target) in targets)
        {
            ValidateTarget(target);
            Require(Regex.IsMatch(branch, @"\A\d{3}\z") && target.Build.StartsWith(branch + ".", StringComparison.Ordinal),
                "Rider branch and exact build must match");
        }
        return targets;
    }

    private static void ValidateTarget(RiderTarget target)
    {
        Require(target is not null && !string.IsNullOrEmpty(target.Version) && !string.IsNullOrEmpty(target.Build) &&
            !string.IsNullOrEmpty(target.Url) && !string.IsNullOrEmpty(target.Sha256), "Incomplete Rider target");
        Require(Regex.IsMatch(target!.Build, @"\A\d{3}\.\d+\.\d+\z"), "Expected an exact numeric Rider build");
        Require(Regex.IsMatch(target.Version, @"\A\d{4}\.\d+(?:\.\d+)*\z"), "Unexpected Rider version");
        Require(Regex.IsMatch(target.Sha256, @"\A[0-9a-f]{64}\z"), "Expected a pinned SHA-256");
        Require(target.Url == $"https://download.jetbrains.com/rider/JetBrains.Rider-{target.Version}.tar.gz",
            "Only the pinned official Linux x64 Rider URL is accepted");
    }

    public static void ValidateHome(string home, RiderTarget target)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "product-info.json")));
        var info = document.RootElement;
        var actual = File.ReadAllText(Path.Combine(home, "build.txt")).Trim();
        if (actual.StartsWith("RD-", StringComparison.Ordinal))
        {
            actual = actual[3..];
        }
        Require(actual == target.Build && info.GetProperty("buildNumber").GetString() == actual,
            $"SDK build mismatch: expected {target.Build}, got {actual}");
        Require(info.GetProperty("productCode").GetString() == "RD" && info.GetProperty("version").GetString() == target.Version,
            "Unexpected Rider product/version");
        Require(Directory.Exists(Path.Combine(home, "lib")) && File.Exists(Path.Combine(home, "jbr", "bin", "java")),
            "Incomplete Linux SDK: lib or bundled runtime is missing");
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static async Task DownloadAsync(RiderTarget target, string destination, HttpClient client,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            ValidateHome(destination, target);
            return;
        }
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Invalid SDK destination");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, "rider-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var archive = Path.Combine(temporary, "rider.tar.gz");
            Console.WriteLine($"Downloading Rider {target.Version} (several GB)");
            using (var response = await client.GetAsync(target.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(output, cancellationToken);
            }
            Require(Sha256File(archive) == target.Sha256, "Rider archive SHA-256 mismatch; refusing to extract");
            var unpacked = Path.Combine(temporary, "unpacked");
            Directory.CreateDirectory(unpacked);
            ExtractArchive(archive, unpacked);
            var candidates = Directory.GetDirectories(unpacked)
                .Where(directory => File.Exists(Path.Combine(directory, "product-info.json"))).ToArray();
            Require(candidates.Length == 1, "Expected one Rider directory in the archive");
            ValidateHome(candidates[0], target);
            // Move only a fully validated SDK. Existing destinations are never overwritten.
            Directory.Move(candidates[0], destination);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    public static void ExtractArchive(string archive, string unpacked)
    {
        Require(Directory.Exists(unpacked) && new DirectoryInfo(unpacked).LinkTarget is null &&
            !Directory.EnumerateFileSystemEntries(unpacked).Any(), "Extraction requires a new, empty directory without a link");
        // Inspect the whole link graph before writing anything. Lexical GetFullPath
        // alone is not safe: link/../.. must resolve link BEFORE walking parents.
        var paths = new List<string>();
        var links = new Dictionary<string, ArchiveLink>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        using (var input = File.OpenRead(archive))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                Require(entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or
                    TarEntryType.Directory or TarEntryType.SymbolicLink or TarEntryType.HardLink,
                    $"Unsupported tar entry type: {entry.EntryType}");
                Require(!entry.Name.Split('/').Contains(".."), "Archive entry names cannot traverse parent directories");
                var entryPath = SafeArchivePath(unpacked, entry.Name);
                var relative = Path.GetRelativePath(unpacked, entryPath).Replace('\\', '/');
                paths.Add(relative);
                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    // Validate the target's basic shape without using lexical
                    // normalization as proof that its physical destination is safe.
                    var linkBase = entry.EntryType == TarEntryType.SymbolicLink ? Path.GetDirectoryName(entryPath)! : unpacked;
                    SafeArchivePath(linkBase, entry.LinkName, unpacked);
                    Require(links.TryAdd(relative, new ArchiveLink(entry.LinkName, entry.EntryType == TarEntryType.HardLink)),
                        "Duplicate archive links are not accepted");
                }
            }
        }
        foreach (var path in paths)
        {
            var segments = path.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                Require(!links.ContainsKey(string.Join('/', segments.Take(index))),
                    "Archive entries may not write through link ancestors");
            }
        }
        foreach (var (name, link) in links)
        {
            var start = link.FromRoot ? new List<string>() : name.Split('/').SkipLast(1).ToList();
            var budget = 128;
            var resolved = ResolveLinkPath(start, link.Target, links, new HashSet<string>(links.Comparer), ref budget);
            Require(resolved.Count > 0, "Archive link must refer to an entry inside the SDK");
        }
        using var stream = File.OpenRead(archive);
        using var compressed = new GZipStream(stream, CompressionMode.Decompress);
        // Retain .NET's own containment checks and Unix permission/link handling.
        TarFile.ExtractToDirectory(compressed, unpacked, overwriteFiles: false);
    }

    private sealed record ArchiveLink(string Target, bool FromRoot);

    private static List<string> ResolveLinkPath(List<string> current, string target,
        Dictionary<string, ArchiveLink> links, HashSet<string> visiting, ref int budget)
    {
        foreach (var component in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }
            if (component == "..")
            {
                Require(current.Count > 0, "Archive link escapes the extraction directory");
                current.RemoveAt(current.Count - 1);
                continue;
            }
            current.Add(component);
            var name = string.Join('/', current);
            if (!links.TryGetValue(name, out var link))
            {
                continue;
            }
            Require(--budget >= 0 && visiting.Add(name), "Archive contains a cyclic or excessively deep link");
            var start = link.FromRoot ? new List<string>() : current.SkipLast(1).ToList();
            current = ResolveLinkPath(start, link.Target, links, visiting, ref budget);
            visiting.Remove(name);
        }
        return current;
    }

    private static string SafeArchivePath(string baseDirectory, string name, string? root = null)
    {
        Require(!string.IsNullOrEmpty(name) && !name.Contains('\\') && !name.Contains(':') && !Path.IsPathRooted(name),
            "Archive has an absolute or non-portable path");
        var fullRoot = Path.GetFullPath(root ?? baseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, name));
        Require(fullPath == fullRoot || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "Archive path escapes the extraction directory");
        return fullPath;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

public static class PluginPackage
{
    public static int Verify(string path, string build, IReadOnlyDictionary<string, string> properties)
    {
        Require(Regex.IsMatch(build, @"\A\d{3}\.\d+\.\d+\z"), "Expected an exact numeric Rider build");
        using var archive = ZipFile.OpenRead(path);
        RequireUniqueNames(archive);
        var files = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
        Require(files.Length == 1 && Regex.IsMatch(files[0].FullName, @"\Arider-autosave-format/lib/[^/\\]+\.jar\z"),
            "Plugin ZIP must contain only its own JAR");
        using var jarBytes = new MemoryStream(ReadEntry(files[0], 64 * 1024 * 1024));
        using var jar = new ZipArchive(jarBytes, ZipArchiveMode.Read);
        RequireUniqueNames(jar);
        var descriptor = ReadXml(jar, "META-INF/plugin.xml");
        Require(descriptor.Name == "idea-plugin", "Expected an idea-plugin descriptor");
        string Value(string key) => properties.TryGetValue(key, out var value) ? value :
            throw new InvalidDataException($"Missing property {key}");
        Require(descriptor.Element("id")?.Value == Value("pluginId"), "Unexpected plugin ID");
        Require(descriptor.Element("name")?.Value == Value("pluginName"), "Unexpected plugin name");
        Require(descriptor.Element("version")?.Value == Value("pluginVersion") + "." + build, "Unexpected per-SDK version");
        Require(descriptor.Element("vendor")?.Value == Value("pluginVendor"), "Unexpected publisher");
        var bounds = descriptor.Element("idea-version");
        Require(bounds is not null && bounds.Attributes().Count() == 3 &&
            bounds.Attribute("since-build")?.Value == build && bounds.Attribute("until-build")?.Value == build &&
            bounds.Attribute("strict-until-build")?.Value == build, "Descriptor must have all three exact compatibility bounds");
        Require(descriptor.Element("incompatible-with")?.Value == "local.rider.autosave.format", "Missing old-plugin migration guard");
        Require(descriptor.Element("depends")?.Value == "com.intellij.modules.rider", "Missing Rider module dependency");
        Require(Encoding.UTF8.GetString(ReadRequiredEntry(jar, "META-INF/LICENSE", 1024 * 1024))
            .Contains("Permission is hereby granted", StringComparison.Ordinal), "Missing MIT license");
        var icon = ReadXml(jar, "META-INF/pluginIcon.svg");
        Require(icon.Attribute("width")?.Value == "40" && icon.Attribute("height")?.Value == "40", "Expected 40x40 SVG icon");
        var classes = jar.Entries.Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal)).ToArray();
        Require(classes.Length > 0, "No compiled plugin code");
        foreach (var entry in classes)
        {
            Require(entry.FullName.StartsWith("local/rider/autosave/", StringComparison.Ordinal) &&
                !entry.FullName.Contains("..", StringComparison.Ordinal) && !entry.FullName.Contains('\\'),
                "Foreign runtime code was bundled");
            var code = ReadEntry(entry, 32 * 1024 * 1024);
            Require(code.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(code) == 0xcafebabe &&
                BinaryPrimitives.ReadUInt16BigEndian(code.AsSpan(6)) == 65, "Expected Java 21 class files");
        }
        return classes.Length;
    }

    private static XElement ReadXml(ZipArchive archive, string name)
    {
        using var stream = new MemoryStream(ReadRequiredEntry(archive, name, 1024 * 1024));
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        try
        {
            return XElement.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Invalid XML in {name}", exception);
        }
    }

    private static byte[] ReadRequiredEntry(ZipArchive archive, string name, int limit) =>
        ReadEntry(archive.GetEntry(name) ?? throw new InvalidDataException($"Missing {name}"), limit);

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        Require(entry.Length <= limit, $"Unexpectedly large archive entry: {entry.FullName}");
        using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, "Archive entry size mismatch");
        return bytes;
    }

    private static void RequireUniqueNames(ZipArchive archive) =>
        Require(archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() == archive.Entries.Count,
            "Duplicate ZIP entries are not accepted");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

public static class ToolCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["-h"])
            {
                Console.WriteLine("Repository tools (.NET 10, no external packages)\n" +
                    "  download-rider <target> <destination> [--github-env] [--root <repository>]\n" +
                    "  check-release-metadata [--root <repository>]\n" +
                    "  verify-plugin-zip <zip> <exact-build> [--root <repository>]");
                return args.Length == 0 ? 2 : 0;
            }
            var arguments = args.ToList();
            string? root = null;
            var rootOption = arguments.IndexOf("--root");
            if (rootOption >= 0)
            {
                if (rootOption + 1 >= arguments.Count)
                {
                    throw new ArgumentException("--root requires a repository directory");
                }
                root = Path.GetFullPath(arguments[rootOption + 1]);
                arguments.RemoveRange(rootOption, 2);
            }
            root ??= RepositoryRoot.Find();
            switch (arguments.ToArray())
            {
                case ["check-release-metadata"]:
                    var errors = ReleaseMetadata.Validate(PropertiesFile.Read(Path.Combine(root, "gradle.properties")), root);
                    if (errors.Count > 0)
                    {
                        throw new InvalidDataException("Release metadata is incomplete:\n- " + string.Join("\n- ", errors));
                    }
                    Console.WriteLine("PASS release metadata. Complete manual runtime checks and Marketplace listing review before submission.");
                    return 0;
                case ["verify-plugin-zip", var zip, var build]:
                    var classes = PluginPackage.Verify(zip, build, PropertiesFile.Read(Path.Combine(root, "gradle.properties")));
                    Console.WriteLine($"PASS package validation: {Path.GetFileName(zip)}, RD-{build}, {classes} own classes");
                    Console.WriteLine($"SHA256 {RiderSdk.Sha256File(zip)}");
                    Console.WriteLine("Scope: archive structure and metadata only; no IDE runtime executed.");
                    return 0;
                case ["download-rider", var targetKey, var destination, .. var options] when options.Length == 0 || options is ["--github-env"]:
                    var targets = RiderSdk.ReadTargets(Path.Combine(root, "scripts", "rider-targets.json"));
                    if (!targets.TryGetValue(targetKey, out var target))
                    {
                        throw new ArgumentException($"Unknown Rider target: {targetKey}");
                    }
                    destination = Path.GetFullPath(destination);
                    if (destination.Contains('\n') || destination.Contains('\r'))
                    {
                        throw new InvalidDataException("Invalid SDK path");
                    }
                    using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
                    { Timeout = TimeSpan.FromMinutes(30) })
                    {
                        await RiderSdk.DownloadAsync(target, destination, client);
                    }
                    Console.WriteLine($"Verified RD-{target.Build}: {destination}");
                    if (arguments.Count == 4)
                    {
                        var githubEnvironment = Environment.GetEnvironmentVariable("GITHUB_ENV") ??
                            throw new InvalidDataException("GITHUB_ENV is not set");
                        await File.AppendAllTextAsync(githubEnvironment, $"RIDER_HOME={destination}\nRIDER_BUILD={target.Build}\n", new UTF8Encoding(false));
                    }
                    return 0;
                default:
                    throw new ArgumentException("Unknown command or arguments. Use --help.");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or
            JsonException or XmlException or HttpRequestException or TaskCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }
}