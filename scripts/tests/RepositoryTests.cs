using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RepositoryTools;

namespace RepositoryTools.Tests;

internal static class RepositoryTests
{
    private static readonly string s_root = RepositoryRoot.Find();
    private static readonly List<TestCase> s_cases = [];
    private static readonly RiderTarget s_target = new("2026.1.3", "261.25134.178",
        "https://download.jetbrains.com/rider/JetBrains.Rider-2026.1.3.tar.gz", new string('0', 64));

    public static async Task<int> RunAsync()
    {
        RegisterMetadataTests();
        RegisterSdkTests();
        RegisterArchiveTests();
        RegisterPackageTests();
        RegisterCommandTests();
        var failed = 0;
        var skipped = 0;
        foreach (var test in s_cases)
        {
            if (test.LinuxOnly && !OperatingSystem.IsLinux())
            {
                Console.WriteLine($"SKIP {test.Name} (requires Linux link/permission semantics)");
                skipped++;
                continue;
            }
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}\n{error}");
            }
        }
        Console.WriteLine($"{s_cases.Count - failed - skipped} passed; {failed} failed; {skipped} skipped.");
        return failed == 0 ? 0 : 1;
    }

    private static void Add(string name, Action test, bool linuxOnly = false) =>
        s_cases.Add(new(name, () =>
        {
            test();
            return Task.CompletedTask;
        }, linuxOnly));

    private static void AddAsync(string name, Func<Task> test) => s_cases.Add(new(name, test));

    private static Dictionary<string, string> PublicMetadata()
    {
        var values = PropertiesFile.Read(Path.Combine(s_root, "gradle.properties"));
        values["pluginVendorEmail"] = "maintainer@publisher.test";
        values["pluginRepositoryUrl"] = "https://github.com/publisher/source";
        return values;
    }

    private static void RegisterMetadataTests()
    {
        Add("targets_have_exact_builds_and_checksums", () =>
        {
            var targets = RiderSdk.ReadTargets(Path.Combine(s_root, "scripts", "rider-targets.json"));
            True(targets.Keys.ToHashSet().SetEquals(["261", "261-latest", "262-first", "262"]), "Expected all four compatibility-check targets");
            foreach (var (key, target) in targets)
            {
                var branch = key.Split('-')[0];
                True(Regex.IsMatch(target.Build, $@"^{branch}\.\d+\.\d+$"), "Build must be exact");
                True(Regex.IsMatch(target.Sha256, "^[0-9a-f]{64}$"), "Expected a pinned SHA-256");
                Equal($"https://download.jetbrains.com/rider/JetBrains.Rider-{target.Version}.tar.gz", target.Url);
            }
        });
        Add("sdk_metadata_mismatch_is_rejected", () =>
        {
            using var temp = new TempDirectory();
            CreateSdkHome(temp.Path, s_target with { Build = "261.1.3" });
            Invalid(() => RiderSdk.ValidateHome(temp.Path, s_target));
        });
        Add("checksum_is_computed_from_bytes", () =>
        {
            using var temp = new TempDirectory();
            var path = temp.File("archive");
            File.WriteAllBytes(path, "checked archive"u8.ToArray());
            Equal(Hash(File.ReadAllBytes(path)), RiderSdk.Sha256File(path));
        });
        AddAsync("wrong_download_checksum_is_rejected_before_extraction", async () =>
        {
            using var temp = new TempDirectory();
            // Deliberately not a tar/gzip: the checksum error must precede any extraction error.
            using var handler = new StubHttpHandler("wrong bytes"u8.ToArray());
            using var client = new HttpClient(handler);
            var error = await InvalidAsync(() => RiderSdk.DownloadAsync(s_target, temp.File("sdk"), client));
            True(error.Message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase), "Expected checksum error before extraction");
            Equal(1, handler.RequestCount);
            Empty(temp.Path);
        });
        Add("invalid_optional_email_is_rejected", () =>
        {
            var values = PublicMetadata();
            values["pluginVendorEmail"] = "invalid";
            True(ReleaseMetadata.Validate(values, s_root).Count > 0, "Invalid email must fail");
        });
        Add("complete_metadata_passes", () => Equal(0, ReleaseMetadata.Validate(PublicMetadata(), s_root).Count));
        Add("email_is_optional", () =>
        {
            var values = PublicMetadata();
            values["pluginVendorEmail"] = "";
            Equal(0, ReleaseMetadata.Validate(values, s_root).Count);
        });
        Add("account_url_is_not_source_repository", () =>
        {
            var values = PublicMetadata();
            values["pluginRepositoryUrl"] = "https://github.com/AndanteTribe";
            True(ReleaseMetadata.Validate(values, s_root).Count > 0, "An account URL is not a repository");
        });
        Add("reserved_name_is_rejected", () =>
        {
            var values = PublicMetadata();
            values["pluginName"] = "Rider Formatter";
            True(ReleaseMetadata.Validate(values, s_root).Count > 0, "Reserved names must fail");
        });
        Add("development_id_is_rejected", () =>
        {
            var values = PublicMetadata();
            values["pluginId"] = "com.example.formatter";
            True(ReleaseMetadata.Validate(values, s_root).Count > 0, "Development IDs must fail");
        });
        foreach (var suffix in new[] { ".261.25134.178", "-rc.1", "+build.1" })
        {
            Add($"release_rejects_version_suffix_{suffix}", () =>
            {
                var values = PublicMetadata();
                values["pluginVersion"] += suffix;
                True(ReleaseMetadata.Validate(values, s_root).Count > 0, "Release versions must not have SDK or prerelease suffixes");
            });
        }
        foreach (var version in new[] { "", "0.1", "v0.1.0", "00.1.0", "0.1.0\n" })
        {
            Add($"release_rejects_malformed_version_{version}", () =>
            {
                var values = PublicMetadata();
                values["pluginVersion"] = version;
                True(ReleaseMetadata.Validate(values, s_root).Count > 0, "A plain release version is required");
            });
        }
        foreach (var bound in new[] { "pluginSinceBuild", "pluginUntilBuild" })
        {
            Add($"release_rejects_inexact_compatibility_{bound}", () =>
            {
                var values = PublicMetadata();
                values[bound] = "262.*";
                True(ReleaseMetadata.Validate(values, s_root).Count > 0, "Compatibility bounds must be exact");
            });
        }
        Add("release_rejects_reversed_compatibility_range", () =>
        {
            var values = PublicMetadata();
            (values["pluginSinceBuild"], values["pluginUntilBuild"]) = (values["pluginUntilBuild"], values["pluginSinceBuild"]);
            True(ReleaseMetadata.Validate(values, s_root).Count > 0, "Compatibility bounds must be ordered");
        });
        Add("properties_preserve_equals_comments_and_unicode", () =>
        {
            using var temp = new TempDirectory();
            var path = temp.File("gradle.properties");
            File.WriteAllText(path, "# ignored\n! also ignored\n key = first=second \nempty=\nname=Auto-save C# Formatter\nkey=最後=value\nnot a property\n");
            var values = PropertiesFile.Read(path);
            Equal(3, values.Count);
            Equal("最後=value", values["key"]);
            Equal("", values["empty"]);
            Equal("Auto-save C# Formatter", values["name"]);
        });
        Add("release_requires_https_and_mit_license", () =>
        {
            var values = PublicMetadata();
            values["pluginVendorUrl"] = "http://publisher.test";
            True(ReleaseMetadata.Validate(values, s_root).Count > 0, "Insecure publisher URL must fail");
            using var temp = new TempDirectory();
            File.WriteAllText(temp.File("LICENSE"), "All rights reserved");
            True(ReleaseMetadata.Validate(PublicMetadata(), temp.Path).Count > 0, "Non-MIT license must fail");
        });
    }

    private static void RegisterSdkTests()
    {
        Add("valid_sdk_home_passes", () =>
        {
            using var temp = new TempDirectory();
            CreateSdkHome(temp.Path, s_target);
            RiderSdk.ValidateHome(temp.Path, s_target);
        });
        foreach (var (name, edit) in new (string, Action<string>)[]
        {
            ("wrong_product", home => WriteProductInfo(home, s_target, "IU")),
            ("wrong_marketing_version", home => WriteProductInfo(home, s_target with { Version = "2026.2" })),
            ("product_build_disagreement", home => WriteProductInfo(home, s_target with { Build = "261.1.2" })),
            ("missing_lib", home => Directory.Delete(Path.Combine(home, "lib"))),
            ("missing_java", home => File.Delete(Path.Combine(home, "jbr", "bin", "java"))),
        })
        {
            Add($"sdk_home_rejects_{name}", () =>
            {
                using var temp = new TempDirectory();
                CreateSdkHome(temp.Path, s_target);
                edit(temp.Path);
                Invalid(() => RiderSdk.ValidateHome(temp.Path, s_target));
            });
        }
        AddAsync("valid_sdk_cache_is_reused_without_network", async () =>
        {
            using var temp = new TempDirectory();
            var home = temp.File("sdk");
            CreateSdkHome(home, s_target);
            File.WriteAllText(Path.Combine(home, "sentinel"), "keep existing cache");
            using var handler = new StubHttpHandler([], failOnRequest: true);
            using var client = new HttpClient(handler);
            await RiderSdk.DownloadAsync(s_target, home, client);
            Equal(0, handler.RequestCount);
            Equal("keep existing cache", File.ReadAllText(Path.Combine(home, "sentinel")));
            Equal(1, Directory.GetFileSystemEntries(temp.Path).Length);
        });
        AddAsync("invalid_sdk_cache_is_rejected_without_network_or_deletion", async () =>
        {
            using var temp = new TempDirectory();
            var home = temp.File("sdk");
            CreateSdkHome(home, s_target with { Build = "261.1.2" });
            using var handler = new StubHttpHandler([], failOnRequest: true);
            using var client = new HttpClient(handler);
            await InvalidAsync(() => RiderSdk.DownloadAsync(s_target, home, client));
            Equal(0, handler.RequestCount);
            True(Directory.Exists(home), "Invalid cache must remain available for diagnosis");
            Equal("RD-261.1.2", File.ReadAllText(Path.Combine(home, "build.txt")));
            Equal(1, Directory.GetFileSystemEntries(temp.Path).Length);
        });
        AddAsync("download_verifies_then_atomically_installs_and_cleans_temporary_files", async () =>
        {
            using var temp = new TempDirectory();
            var destination = temp.File("sdk with spaces");
            var bytes = SdkArchive(s_target);
            var target = s_target with { Sha256 = Hash(bytes) };
            using var handler = new StubHttpHandler(bytes, onRequest: () =>
                True(!Directory.Exists(destination), "Destination must not be published before validation"));
            using var client = new HttpClient(handler);
            await RiderSdk.DownloadAsync(target, destination, client);
            Equal(1, handler.RequestCount);
            Equal(new Uri(target.Url), handler.LastUri);
            RiderSdk.ValidateHome(destination, target);
            Equal("runtime", File.ReadAllText(Path.Combine(destination, "jbr", "bin", "java")));
            Equal(1, Directory.GetFileSystemEntries(temp.Path).Length);
        });
        AddAsync("download_rejects_metadata_mismatch_and_cleans_temporary_files", async () =>
        {
            using var temp = new TempDirectory();
            var bytes = SdkArchive(s_target with { Build = "261.1.2" });
            using var handler = new StubHttpHandler(bytes);
            using var client = new HttpClient(handler);
            await InvalidAsync(() => RiderSdk.DownloadAsync(s_target with { Sha256 = Hash(bytes) }, temp.File("sdk"), client));
            Empty(temp.Path);
        });
        AddAsync("download_rejects_multiple_sdk_roots_and_cleans_temporary_files", async () =>
        {
            using var temp = new TempDirectory();
            var bytes = TarGzip(SdkEntries(s_target, "first").Concat(SdkEntries(s_target, "second")));
            using var handler = new StubHttpHandler(bytes);
            using var client = new HttpClient(handler);
            await InvalidAsync(() => RiderSdk.DownloadAsync(s_target with { Sha256 = Hash(bytes) }, temp.File("sdk"), client));
            Empty(temp.Path);
        });
        AddAsync("http_failure_does_not_publish_or_leave_temporary_files", async () =>
        {
            using var temp = new TempDirectory();
            using var handler = new StubHttpHandler([], status: HttpStatusCode.NotFound);
            using var client = new HttpClient(handler);
            await ThrowsAsync<HttpRequestException>(() => RiderSdk.DownloadAsync(s_target, temp.File("sdk"), client));
            Empty(temp.Path);
        });
        AddAsync("canceled_download_does_not_publish_or_leave_temporary_files", async () =>
        {
            using var temp = new TempDirectory();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var handler = new StubHttpHandler(SdkArchive(s_target));
            using var client = new HttpClient(handler);
            await ThrowsAsync<OperationCanceledException>(() => RiderSdk.DownloadAsync(s_target, temp.File("sdk"), client, cancellation.Token));
            Empty(temp.Path);
        });
    }

    private static void RegisterArchiveTests()
    {
        foreach (var unsafeName in new[] { "../outside", "rider/../../outside", "/absolute", "C:/rooted", @"rider\..\outside" })
        {
            Add($"tar_rejects_unsafe_path_{unsafeName}", () =>
            {
                using var temp = new TempDirectory();
                var archive = temp.File("fixture.tar.gz");
                File.WriteAllBytes(archive, TarGzip([TarFile(unsafeName, "escape")]));
                Invalid(() => ExtractFixture(archive, temp.File("unpacked")));
                True(!File.Exists(temp.File("outside")), "Extraction must not escape its destination");
            });
        }
        foreach (var type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink })
        {
            foreach (var target in new[] { "../../outside", "/outside", "C:/outside", @"..\outside" })
            {
                Add($"tar_rejects_escaping_{type}_{target}", () =>
                {
                    using var temp = new TempDirectory();
                    var archive = temp.File("fixture.tar.gz");
                    File.WriteAllBytes(archive, TarGzip([new TarSpec(type, "rider/link", LinkName: target)]));
                    Invalid(() => ExtractFixture(archive, temp.File("unpacked")));
                    True(!File.Exists(temp.File("outside")), "Link must not escape extraction root");
                });
            }
        }
        foreach (var type in new[] { TarEntryType.Fifo, TarEntryType.BlockDevice, TarEntryType.CharacterDevice })
        {
            Add($"tar_rejects_special_file_{type}", () =>
            {
                using var temp = new TempDirectory();
                var archive = temp.File("fixture.tar.gz");
                File.WriteAllBytes(archive, TarGzip([new TarSpec(type, "rider/special")]));
                Invalid(() => ExtractFixture(archive, temp.File("unpacked")));
            });
        }
        Add("tar_rejects_malformed_gzip", () =>
        {
            using var temp = new TempDirectory();
            var archive = temp.File("fixture.tar.gz");
            File.WriteAllBytes(archive, "not an archive"u8.ToArray());
            Invalid(() => ExtractFixture(archive, temp.File("unpacked")));
        });
        Add("tar_rejects_writes_through_an_escaping_symlink", () =>
        {
            using var temp = new TempDirectory();
            var archive = temp.File("fixture.tar.gz");
            File.WriteAllBytes(archive, TarGzip([
                new TarSpec(TarEntryType.SymbolicLink, "rider/link", LinkName: "../../"),
                TarFile("rider/link/outside", "escape"),
            ]));
            Invalid(() => ExtractFixture(archive, temp.File("unpacked")));
            True(!File.Exists(temp.File("outside")), "Extraction wrote through an escaping symlink");
        });
        Add("tar_preserves_safe_symbolic_and_hard_links_and_executable_mode", () =>
        {
            using var temp = new TempDirectory();
            var archive = temp.File("fixture.tar.gz");
            File.WriteAllBytes(archive, TarGzip([
                new TarSpec(TarEntryType.Directory, "rider"),
                new TarSpec(TarEntryType.Directory, "rider/bin"),
                TarFile("rider/bin/java", "runtime", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                new TarSpec(TarEntryType.SymbolicLink, "rider/bin/java-link", LinkName: "java"),
                new TarSpec(TarEntryType.HardLink, "rider/bin/java-hard", LinkName: "rider/bin/java"),
            ]));
            var unpacked = temp.File("unpacked");
            ExtractFixture(archive, unpacked);
            var original = Path.Combine(unpacked, "rider", "bin", "java");
            var symbolic = Path.Combine(unpacked, "rider", "bin", "java-link");
            var hard = Path.Combine(unpacked, "rider", "bin", "java-hard");
            Equal("java", new FileInfo(symbolic).LinkTarget);
            Equal("runtime", File.ReadAllText(symbolic));
            Equal("runtime", File.ReadAllText(hard));
            if (OperatingSystem.IsLinux())
            {
                True((File.GetUnixFileMode(original) & UnixFileMode.UserExecute) != 0, "Executable bit must survive extraction");
            }
            File.WriteAllText(original, "updated runtime");
            Equal("updated runtime", File.ReadAllText(symbolic));
            Equal("updated runtime", File.ReadAllText(hard));
        }, linuxOnly: true);
    }

    private static void RegisterPackageTests()
    {
        Add("package_accepts_java21_bounded_range_identity_license_icon_and_rider_dependency", () =>
        {
            using var temp = new TempDirectory();
            var fixture = new PackageFixture();
            Equal(2, PluginPackage.Verify(fixture.Write(temp.File("plugin.zip")), s_target.Build, fixture.Properties));
        });
        foreach (var field in new[] { "id", "name", "version", "vendor" })
        {
            InvalidDescriptor($"package_rejects_wrong_{field}", xml => xml.Root!.Element(field)!.Value = "wrong");
        }
        foreach (var bound in new[] { "since-build", "until-build", "strict-until-build" })
        {
            InvalidDescriptor($"package_requires_{bound}", xml => xml.Root!.Element("idea-version")!.Attribute(bound)!.Remove());
            InvalidDescriptor($"package_rejects_inexact_{bound}", xml => xml.Root!.Element("idea-version")!.SetAttributeValue(bound, "261.*"));
        }
        InvalidDescriptor("package_rejects_extra_compatibility_attributes", xml => xml.Root!.Element("idea-version")!.SetAttributeValue("unexpected", "true"));
        InvalidDescriptor("package_rejects_numeric_sdk_suffix", xml => xml.Root!.Element("version")!.Value =
            PublicMetadata()["pluginVersion"] + "." + s_target.Build);
        InvalidDescriptor("package_rejects_prerelease_version", xml => xml.Root!.Element("version")!.Value =
            PublicMetadata()["pluginVersion"] + "-rc." + s_target.Build);
        InvalidDescriptor("package_rejects_sdk_version_metadata", xml => xml.Root!.Element("version")!.Value =
            PublicMetadata()["pluginVersion"] + "+" + s_target.Build);
        Add("package_accepts_same_artifact_at_both_range_endpoints", () =>
        {
            using var temp = new TempDirectory();
            var fixture = new PackageFixture();
            var path = fixture.Write(temp.File("plugin.zip"));
            foreach (var bound in new[] { "pluginSinceBuild", "pluginUntilBuild" })
            {
                Equal(2, PluginPackage.Verify(path, fixture.Properties[bound], fixture.Properties));
            }
        });
        foreach (var build in new[] { "261.1.1", "262.10968.171", "263.1.1" })
        {
            Add($"package_rejects_out_of_range_build_{build}", () =>
            {
                using var temp = new TempDirectory();
                var fixture = new PackageFixture();
                Invalid(() => PluginPackage.Verify(fixture.Write(temp.File("plugin.zip")), build, fixture.Properties));
            });
        }
        InvalidDescriptor("package_requires_rider_dependency", xml => xml.Root!.Element("depends")!.Remove());
        InvalidDescriptor("package_rejects_wrong_rider_dependency", xml => xml.Root!.Element("depends")!.Value = "com.intellij.modules.platform");
        InvalidDescriptor("package_requires_old_plugin_migration_guard", xml => xml.Root!.Element("incompatible-with")!.Remove());
        InvalidDescriptor("package_rejects_wrong_migration_guard", xml => xml.Root!.Element("incompatible-with")!.Value = "another.plugin");
        foreach (var file in new[] { "META-INF/plugin.xml", "META-INF/LICENSE", "META-INF/pluginIcon.svg" })
        {
            InvalidPackage($"package_requires_{file}", fixture => fixture.Jar.RemoveAll(entry => entry.Name == file));
        }
        InvalidPackage("package_requires_mit_license_text", fixture => fixture.Replace("META-INF/LICENSE", "All rights reserved"));
        InvalidPackage("package_requires_40x40_icon", fixture => fixture.Replace("META-INF/pluginIcon.svg", "<svg width=\"32\" height=\"40\"/>"));
        InvalidPackage("package_rejects_malformed_icon_xml", fixture => fixture.Replace("META-INF/pluginIcon.svg", "not xml"));
        InvalidPackage("package_rejects_malformed_descriptor_xml", fixture => fixture.Replace("META-INF/plugin.xml", "<idea-plugin>"));
        InvalidPackage("package_requires_compiled_classes", fixture => fixture.Jar.RemoveAll(entry => entry.Name.EndsWith(".class", StringComparison.Ordinal)));
        InvalidPackage("package_rejects_foreign_runtime_class", fixture => fixture.Jar.Add(new("kotlin/Unit.class", ClassBytes())));
        InvalidPackage("package_rejects_bad_class_magic", fixture => fixture.Replace("local/rider/autosave/SaveGate.class", new byte[8]));
        InvalidPackage("package_rejects_java17_class", fixture => fixture.Replace("local/rider/autosave/SaveGate.class", ClassBytes(61)));
        InvalidPackage("package_rejects_java25_class", fixture => fixture.Replace("local/rider/autosave/SaveGate.class", ClassBytes(69)));
        InvalidPackage("package_rejects_truncated_class_header", fixture => fixture.Replace("local/rider/autosave/SaveGate.class", new byte[] { 0xca, 0xfe, 0xba, 0xbe }));
        InvalidPackage("package_rejects_duplicate_jar_entries", fixture => fixture.Jar.Add(fixture.Jar[0]));
        InvalidPackage("package_rejects_duplicate_class_entries", fixture => fixture.Jar.Add(new("local/rider/autosave/SaveGate.class", ClassBytes())));
        InvalidPackage("package_rejects_extra_outer_file", fixture => fixture.ExtraOuter.Add(new("README.txt", "extra"u8.ToArray())));
        InvalidPackage("package_rejects_extra_runtime_jar", fixture => fixture.ExtraOuter.Add(new("rider-autosave-format/lib/kotlin.jar", [])));
        InvalidPackage("package_rejects_duplicate_outer_entry", fixture => fixture.DuplicateOuter = true);
        InvalidPackage("package_rejects_foreign_outer_root", fixture => fixture.JarPath = "foreign/lib/plugin.jar");
        InvalidPackage("package_rejects_outer_traversal", fixture => fixture.JarPath = "rider-autosave-format/lib/../../plugin.jar");
    }

    private static void RegisterCommandTests()
    {
        AddAsync("cli_help_lists_supported_commands", async () =>
        {
            var result = await RunCommand("--help");
            Equal(0, result.ExitCode);
            True(result.Output.Contains("download-rider", StringComparison.Ordinal), "Help must document SDK download");
            True(result.Output.Contains("verify-plugin-zip", StringComparison.Ordinal), "Help must document package verification");
        });
        AddAsync("cli_missing_command_returns_usage_error", async () => Equal(2, (await RunCommand()).ExitCode));
        AddAsync("cli_unknown_command_returns_nonzero", async () => Equal(1, (await RunCommand("not-a-command")).ExitCode));
        AddAsync("cli_missing_root_argument_returns_nonzero", async () => Equal(1, (await RunCommand("check-release-metadata", "--root")).ExitCode));
        AddAsync("cli_complete_metadata_passes_with_explicit_root", async () =>
        {
            using var temp = new TempDirectory();
            WriteMetadataRoot(temp.Path, PublicMetadata());
            Equal(0, (await RunCommand("check-release-metadata", "--root", temp.Path)).ExitCode);
        });
        AddAsync("cli_incomplete_metadata_returns_nonzero", async () =>
        {
            using var temp = new TempDirectory();
            var properties = PublicMetadata();
            properties["pluginRepositoryUrl"] = "";
            WriteMetadataRoot(temp.Path, properties);
            var result = await RunCommand("check-release-metadata", "--root", temp.Path);
            Equal(1, result.ExitCode);
            True(result.Error.Contains("pluginRepositoryUrl", StringComparison.Ordinal), "Failure must identify missing metadata");
        });
        AddAsync("cli_package_verification_reports_classes_and_checksum", async () =>
        {
            using var temp = new TempDirectory();
            var package = new PackageFixture().Write(temp.File("plugin.zip"));
            var result = await RunCommand("verify-plugin-zip", package, s_target.Build, "--root", s_root);
            Equal(0, result.ExitCode);
            True(result.Output.Contains("2 own classes", StringComparison.Ordinal), "CLI must report the verified class count");
            True(result.Output.Contains(RiderSdk.Sha256File(package), StringComparison.Ordinal), "CLI must report the artifact checksum");
        });
        AddAsync("cli_package_version_mismatch_returns_nonzero", async () =>
        {
            using var temp = new TempDirectory();
            var package = new PackageFixture().Write(temp.File("plugin.zip"));
            Equal(1, (await RunCommand("verify-plugin-zip", package, "261.1.2", "--root", s_root)).ExitCode);
        });
        AddAsync("cli_unknown_sdk_target_returns_nonzero_without_network", async () =>
        {
            using var temp = new TempDirectory();
            Equal(1, (await RunCommand("download-rider", "999", temp.File("sdk"), "--root", s_root)).ExitCode);
            Empty(temp.Path);
        });
        AddAsync("cli_cached_sdk_appends_github_environment_without_network", async () =>
        {
            using var temp = new TempDirectory();
            var targets = RiderSdk.ReadTargets(Path.Combine(s_root, "scripts", "rider-targets.json"));
            var target = targets["261"];
            var home = temp.File("sdk with spaces");
            CreateSdkHome(home, target);
            var environmentFile = temp.File("github-env");
            File.WriteAllText(environmentFile, "EXISTING=keep\n");
            var previous = Environment.GetEnvironmentVariable("GITHUB_ENV");
            try
            {
                Environment.SetEnvironmentVariable("GITHUB_ENV", environmentFile);
                var result = await RunCommand("download-rider", "261", home, "--github-env", "--root", s_root);
                Equal(0, result.ExitCode);
                Equal($"EXISTING=keep\nRIDER_HOME={home}\nRIDER_BUILD={target.Build}\n", File.ReadAllText(environmentFile));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GITHUB_ENV", previous);
            }
        });
        AddAsync("cli_rejects_environment_newline_in_sdk_path", async () =>
        {
            using var temp = new TempDirectory();
            var result = await RunCommand("download-rider", "261", temp.File("sdk\nINJECTED=value"), "--root", s_root);
            Equal(1, result.ExitCode);
            Empty(temp.Path);
        });
        Add("repository_root_is_found_from_a_nested_directory", () =>
        {
            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = Path.Combine(s_root, "scripts", "tests");
                Equal(s_root, RepositoryRoot.Find());
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        });
        Add("repository_root_fails_outside_the_checkout", () =>
        {
            using var temp = new TempDirectory();
            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = temp.Path;
                Invalid(() => RepositoryRoot.Find());
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        });
    }

    private static void WriteMetadataRoot(string root, IReadOnlyDictionary<string, string> properties)
    {
        File.WriteAllLines(Path.Combine(root, "gradle.properties"), properties.Select(item => item.Key + "=" + item.Value));
        File.Copy(Path.Combine(s_root, "LICENSE"), Path.Combine(root, "LICENSE"));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCommand(params string[] arguments)
    {
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await ToolCommands.RunAsync(arguments);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }

    private static void InvalidDescriptor(string name, Action<XDocument> mutate) => InvalidPackage(name, fixture =>
    {
        var xml = XDocument.Parse(Encoding.UTF8.GetString(fixture.Jar.Single(entry => entry.Name == "META-INF/plugin.xml").Bytes));
        mutate(xml);
        fixture.Replace("META-INF/plugin.xml", xml.ToString());
    });

    private static void InvalidPackage(string name, Action<PackageFixture> mutate) => Add(name, () =>
    {
        using var temp = new TempDirectory();
        var fixture = new PackageFixture();
        mutate(fixture);
        Invalid(() => PluginPackage.Verify(fixture.Write(temp.File("plugin.zip")), s_target.Build, fixture.Properties));
    });

    private static byte[] ClassBytes(int major = 65) => [0xca, 0xfe, 0xba, 0xbe, 0, 0, (byte)(major >> 8), (byte)major];
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void CreateSdkHome(string home, RiderTarget target)
    {
        Directory.CreateDirectory(Path.Combine(home, "lib"));
        Directory.CreateDirectory(Path.Combine(home, "jbr", "bin"));
        File.WriteAllText(Path.Combine(home, "jbr", "bin", "java"), "runtime");
        File.WriteAllText(Path.Combine(home, "build.txt"), "RD-" + target.Build);
        WriteProductInfo(home, target);
    }

    private static void WriteProductInfo(string home, RiderTarget target, string productCode = "RD") =>
        File.WriteAllText(Path.Combine(home, "product-info.json"), ProductInfo(target, productCode));

    private static string ProductInfo(RiderTarget target, string productCode = "RD") =>
        JsonSerializer.Serialize(new { buildNumber = target.Build, productCode, version = target.Version });

    private static IEnumerable<TarSpec> SdkEntries(RiderTarget target, string root = "rider") =>
    [
        new(TarEntryType.Directory, root),
        new(TarEntryType.Directory, root + "/lib"),
        new(TarEntryType.Directory, root + "/jbr"),
        new(TarEntryType.Directory, root + "/jbr/bin"),
        TarFile(root + "/product-info.json", ProductInfo(target)),
        TarFile(root + "/build.txt", "RD-" + target.Build),
        TarFile(root + "/jbr/bin/java", "runtime"),
    ];

    private static void ExtractFixture(string archive, string unpacked)
    {
        Directory.CreateDirectory(unpacked);
        RiderSdk.ExtractArchive(archive, unpacked);
    }

    private static byte[] SdkArchive(RiderTarget target) => TarGzip(SdkEntries(target));
    private static TarSpec TarFile(string name, string content, UnixFileMode? mode = null) =>
        new(TarEntryType.RegularFile, name, Encoding.UTF8.GetBytes(content), Mode: mode);

    private static byte[] TarGzip(IEnumerable<TarSpec> entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = new PaxTarEntry(item.Type, item.Name);
                if (item.Bytes is not null)
                {
                    entry.DataStream = new MemoryStream(item.Bytes, writable: false);
                }
                if (item.LinkName is not null)
                {
                    entry.LinkName = item.LinkName;
                }
                if (item.Mode is not null)
                {
                    entry.Mode = item.Mode.Value;
                }
                writer.WriteEntry(entry);
                entry.DataStream?.Dispose();
            }
        }
        return output.ToArray();
    }

    private static byte[] ZipBytes(IEnumerable<ZipItem> entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                using var stream = zip.CreateEntry(item.Name).Open();
                stream.Write(item.Bytes);
            }
        }
        return output.ToArray();
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected <{expected}>; got <{actual}>");

    private static void Empty(string directory) =>
        True(!Directory.EnumerateFileSystemEntries(directory).Any(), "Failed operation left destination or temporary files behind");

    private static Exception Invalid(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            return error;
        }
        throw new InvalidOperationException("Expected invalid data or an I/O failure");
    }

    private static async Task<Exception> InvalidAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            return error;
        }
        throw new InvalidOperationException("Expected invalid data or an I/O failure");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private sealed record TestCase(string Name, Func<Task> Run, bool LinuxOnly = false);
    private sealed record TarSpec(TarEntryType Type, string Name, byte[]? Bytes = null, string? LinkName = null, UnixFileMode? Mode = null);
    private sealed record ZipItem(string Name, byte[] Bytes);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "repository-tools-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class StubHttpHandler(byte[] bytes, bool failOnRequest = false, Action? onRequest = null,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUri = request.RequestUri;
            cancellationToken.ThrowIfCancellationRequested();
            True(!failOnRequest, "Cached SDK unexpectedly triggered an HTTP request");
            onRequest?.Invoke();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class PackageFixture
    {
        public Dictionary<string, string> Properties { get; } = PublicMetadata();
        public List<ZipItem> Jar { get; } = [];
        public List<ZipItem> ExtraOuter { get; } = [];
        public string JarPath { get; set; } = "rider-autosave-format/lib/rider-autosave-format.jar";
        public bool DuplicateOuter { get; set; }

        public PackageFixture()
        {
            var xml = new XElement("idea-plugin",
                new XElement("id", Properties["pluginId"]),
                new XElement("name", Properties["pluginName"]),
                new XElement("version", Properties["pluginVersion"]),
                new XElement("vendor", Properties["pluginVendor"]),
                new XElement("idea-version", new XAttribute("since-build", Properties["pluginSinceBuild"]),
                    new XAttribute("until-build", Properties["pluginUntilBuild"]),
                    new XAttribute("strict-until-build", Properties["pluginUntilBuild"])),
                new XElement("depends", "com.intellij.modules.rider"),
                new XElement("incompatible-with", "local.rider.autosave.format"));
            Jar.Add(new("META-INF/plugin.xml", Encoding.UTF8.GetBytes(xml.ToString())));
            Jar.Add(new("META-INF/LICENSE", Encoding.UTF8.GetBytes("Permission is hereby granted, free of charge, to any person obtaining a copy.")));
            Jar.Add(new("META-INF/pluginIcon.svg", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\"/>")));
            Jar.Add(new("local/rider/autosave/SaveGate.class", ClassBytes()));
            Jar.Add(new("local/rider/autosave/NativeFormattingService.class", ClassBytes()));
        }

        public void Replace(string name, string content) => Replace(name, Encoding.UTF8.GetBytes(content));
        public void Replace(string name, byte[] content)
        {
            var index = Jar.FindIndex(entry => entry.Name == name);
            True(index >= 0, $"Fixture entry not found: {name}");
            Jar[index] = new(name, content);
        }

        public string Write(string path)
        {
            var jar = new ZipItem(JarPath, ZipBytes(Jar));
            var outer = new List<ZipItem> { jar };
            outer.AddRange(ExtraOuter);
            if (DuplicateOuter)
            {
                outer.Add(jar);
            }
            File.WriteAllBytes(path, ZipBytes(outer));
            return path;
        }
    }
}