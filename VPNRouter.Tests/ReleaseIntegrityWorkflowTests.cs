using System.Diagnostics;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace VPNRouter.Tests;

public sealed class ReleaseIntegrityWorkflowTests
{
    [Fact]
    public void Scripts_EnvironmentBindAllEventDataAndRequireTagCommitIdentity()
    {
        foreach (var step in Steps())
            Assert.DoesNotContain("${{", Text(step, "run"));
        var env = (YamlMappingNode)Job().Children[new YamlScalarNode("env")];
        Assert.Equal("${{ inputs.tag }}", Text(env, "INPUT_TAG"));
        Assert.Equal("${{ github.event.release.tag_name }}", Text(env, "RELEASE_TAG"));
        Assert.Equal("${{ github.sha }}", Text(env, "WORKFLOW_SHA"));
        var script = Script("Resolve tag and version");
        Assert.Contains("TAG=\"$INPUT_TAG\"", script);
        Assert.Contains("TAG=\"$RELEASE_TAG\"", script);
        Assert.Contains("[ \"$REF_TYPE\" != \"tag\" ] || [ \"$REF_NAME\" != \"$TAG\" ]", script);
        Assert.Contains("repos/$GITHUB_REPOSITORY/commits/$TAG", script);
        Assert.Contains("[ \"$REMOTE_SHA\" != \"$WORKFLOW_SHA\" ]", script);
        Assert.True(script.IndexOf("exit 1", StringComparison.Ordinal) < script.IndexOf("GITHUB_OUTPUT", StringComparison.Ordinal));
        Assert.Contains("--ref \"$TAG\" -f tag=\"$TAG\"", Source());
    }

    [Fact]
    public void TagGrammar_IsStrictAndMajorNeutral()
    {
        var grammar = Regex.Match(Script("Resolve tag and version"), @"! ""\$TAG"" =~ (.+) \]\]").Groups[1].Value;
        Assert.NotEmpty(grammar);
        foreach (var tag in new[] { "v0.0.0", "v2.49.4-r1", "v10.200.300-r42" })
            Assert.Matches(grammar, tag);
        foreach (var tag in new[] { "", "2.49.4", "v02.49.4", "v2.49", "v2.49.4-r0", "v2.49.4-r01", "v2.49.4;id", "$(id)", "v2.49.4\nother" })
            Assert.DoesNotMatch(grammar, tag);
    }

    [Fact]
    public void Inventory_RequiresExactlyAllSixteenNamesBeforeUnfilteredDownload()
    {
        var script = Script("Require exact inventory and download release assets");
        var names = Regex.Matches(script, "\"(VPNRouter-[^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(16, names.Length);
        Assert.Equal(16, names.Distinct(StringComparer.Ordinal).Count());
        var binaries = new[]
        {
            "VPNRouter-v${EXPECTED_VERSION}-win.zip", "VPNRouter-update-v${EXPECTED_VERSION}-win.zip",
            "VPNRouter-v${EXPECTED_VERSION}-mac.dmg", "VPNRouter-v${EXPECTED_VERSION}-mac.zip",
            "VPNRouter-v${EXPECTED_VERSION}-linux-amd64.deb", "VPNRouter-v${EXPECTED_VERSION}-linux-x86_64.AppImage",
            "VPNRouter-v${EXPECTED_VERSION}-linux.tar.gz", "VPNRouter-v${EXPECTED_VERSION}-android-arm64.apk"
        };
        Assert.Equal(binaries.SelectMany(n => new[] { n, n + ".sha256" }).OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(8, names.Count(n => n.EndsWith(".sha256", StringComparison.Ordinal)));
        foreach (var name in names.Where(n => !n.EndsWith(".sha256", StringComparison.Ordinal)))
            Assert.Contains(name + ".sha256", names);
        Assert.Contains("jq -r '.assets[].name' release-before.json", script);
        Assert.Contains("releases/assets/$asset_id", script);
        Assert.Contains("{id,name,size,updated_at,digest,state}", script);
        Assert.Contains("if ! diff -u expected-assets.txt actual-assets.txt", script);
        Assert.Contains("exit 1", script);
        Assert.DoesNotContain("--pattern", script);
        Assert.True(script.IndexOf("diff -u", StringComparison.Ordinal) < script.IndexOf("releases/assets/$asset_id", StringComparison.Ordinal));
        var verify = Script("Verify integrity");
        Assert.Contains("sorted(p.name for p in root.iterdir()) != sorted(expected)", verify);
        Assert.Contains("is_symlink()", verify);
    }

    [Fact]
    public void Hashes_PrecedeAllParsingAndWindowsEmbeddedVersionRemainsHard()
    {
        var script = Script("Verify integrity");
        Assert.Contains("re.fullmatch(r'([0-9a-fA-F]{64})", script);
        Assert.Contains("match.group(2) != name", script);
        Assert.Contains("hashlib.file_digest(stream, 'sha256')", script);
        var hashes = script.IndexOf("hashlib.file_digest", StringComparison.Ordinal);
        var stop = script.IndexOf("if errors:\n    finish()", hashes, StringComparison.Ordinal);
        var parse = script.IndexOf("with zipfile.ZipFile", StringComparison.Ordinal);
        Assert.True(hashes >= 0 && stop > hashes && parse > stop);
        Assert.Contains("hard = name.endswith('-win.zip')", script);
        Assert.Contains("sink = errors if hard else warnings", script);
        Assert.Contains("not all(version in versions(data) for data in blobs)", script);
        Assert.Contains("raise SystemExit(1)", script);
        Assert.Contains("('utf-16-le', 1)", script);
        Assert.DoesNotContain("r'2\\.", script);
    }

    [Fact]
    public void Inspection_IsDataOnlyAndDoesNotWriteArchivePaths()
    {
        var script = Script("Verify integrity");
        Assert.Contains("embedded version inspection unavailable; hash verified; payload never executed", script);
        Assert.Contains("archive.read(m)", script);
        Assert.Contains("archive.extractfile(m).read()", script);
        Assert.Contains("if m.isfile()", script);
        Assert.Contains("['dpkg-deb', '--fsys-tarfile', str(path)]", script);
        Assert.Contains("['7z', 'x', '-so', str(path)", script);
        foreach (var forbidden in new[] { "--appimage-extract", "chmod +x", "extractall(", "archive.extract(", "shell=True", "unzip -", "tar -x", "ar x ", "7z x -y", "eval " })
            Assert.DoesNotContain(forbidden, Source());
    }

    [Fact]
    public void DryRun_PreventsEveryMutationAndFinalGateNeverSkipsDrafts()
    {
        var flag = Steps().Single(s => Text(s, "name") == "Flag release on failure");
        Assert.Contains("inputs.auto_draft_on_failure", Text(flag, "if"));
        var script = Text(flag, "run");
        var guard = script.IndexOf("[ \"$AUTO_DRAFT_INPUT\" != \"true\" ]", StringComparison.Ordinal);
        var stop = script.IndexOf("exit 0", guard, StringComparison.Ordinal);
        Assert.True(guard >= 0 && stop > guard && stop < script.IndexOf("gh release edit", StringComparison.Ordinal));
        Assert.Equal(2, Regex.Matches(script, "gh release edit").Count);
        foreach (var step in Steps().Where(s => Text(s, "name") != "Flag release on failure"))
        {
            Assert.DoesNotContain("gh release edit", Text(step, "run"));
            Assert.DoesNotContain("skip", Text(step, "if"));
            Assert.DoesNotContain("exit 0", Text(step, "run"));
            Assert.NotEqual("true", Text(step, "continue-on-error"));
        }
        Assert.Contains("types: [published]", Source());
        var final = Script("Confirm final tag-bound inventory");
        Assert.Contains("test \"$REMOTE_SHA\" = \"$WORKFLOW_SHA\"", final);
        Assert.Contains("diff -u expected-assets.txt final-assets.txt", final);
        Assert.Contains("diff -u inventory-before.json inventory-after.json", final);
        Assert.DoesNotContain("isDraft", Script("Resolve tag and version"));
    }

    [Fact]
    public void EmbeddedPython_BenignFixturesCoverHashFirstAndDataOnlyInspection()
    {
        // The workflow runs on Ubuntu; other platforms retain the YAML/source contracts.
        if (!OperatingSystem.IsLinux()) return;
        var script = Script("Verify integrity");
        var python = script[(script.IndexOf("import hashlib", StringComparison.Ordinal))..];
        python = python[..python.LastIndexOf("\nPY", StringComparison.Ordinal)];
        const string fixture = """
            import ast, hashlib, io, os, pathlib, subprocess, sys, tarfile, tempfile, zipfile
            source = sys.stdin.read()
            tree = ast.parse(source)
            # Exercise the actual scanner and data readers without executing workflow/network commands.
            functions = ast.Module(body=[n for n in tree.body if isinstance(n, (ast.Import, ast.FunctionDef))], type_ignores=[])
            scope = {}
            exec(compile(functions, '<workflow-functions>', 'exec'), scope)
            for version in ['2.49.4-r1', '3.0.0', '10.20.300-r42']:
                for prefix in [b'', b'x']:
                    assert version in scope['versions'](prefix + version.encode('utf-16-le'))
            assert '3.0.0' not in scope['versions']('13.0.0'.encode('utf-16-le'))
            assert '3.0.0' not in scope['versions']('3.0.0-r1'.encode('utf-16-le'))
            with tempfile.TemporaryDirectory() as temp:
                root = pathlib.Path(temp)
                archive = root / 'safe.zip'
                with zipfile.ZipFile(archive, 'w') as z:
                    z.writestr('../../escape', 'never written')
                    z.writestr('../../VPNRouter.Core.dll', '3.0.0'.encode('utf-16-le'))
                assert scope['inspect_zip'](archive) == ['3.0.0'.encode('utf-16-le')]
                archive_win = root / 'win.zip'
                with zipfile.ZipFile(archive_win, 'w') as z:
                    z.writestr('app\\VPNRouter.Core.dll', '3.0.0'.encode('utf-16-le'))
                assert scope['inspect_zip'](archive_win) == ['3.0.0'.encode('utf-16-le')]
                assert sorted(p.name for p in root.iterdir()) == ['safe.zip', 'win.zip']
                stream = io.BytesIO()
                with tarfile.open(fileobj=stream, mode='w') as t:
                    link = tarfile.TarInfo('VPNRouter.Core.dll')
                    link.type, link.linkname = tarfile.SYMTYPE, '/etc/passwd'
                    t.addfile(link)
                    data = b'fixture'
                    member = tarfile.TarInfo('../../VPNRouter.Core.dll')
                    member.size = len(data)
                    t.addfile(member, io.BytesIO(data))
                stream.seek(0)
                assert scope['inspect_tar'](stream) == [b'fixture']
                # Hash mismatch must finish before an invalid ZIP can be parsed.
                assets = root / 'assets'
                assets.mkdir()
                name = 'VPNRouter-v3.0.0-win.zip'
                (assets / name).write_bytes(b'not a ZIP')
                sidecar = assets / (name + '.sha256')
                expected = root / 'expected-assets.txt'
                expected.write_text(name + '\n' + name + '.sha256\n')
                env = dict(os.environ, EXPECTED_VERSION='3.0.0', EXPECTED_TAG='v3.0.0',
                           GITHUB_OUTPUT=str(root / 'output'), GITHUB_STEP_SUMMARY=str(root / 'summary'))
                # Replace only the fixed banner destination to keep this fixture isolated.
                isolated = source.replace("'/tmp/failure_banner.md'", repr(str(root / 'banner')))
                for contents in ['0' * 64, 'bad hash', hashlib.sha256(b'not a ZIP').hexdigest() + '  wrong.zip']:
                    sidecar.write_text(contents)
                    result = subprocess.run([sys.executable, '-c', isolated], cwd=root, env=env, capture_output=True, text=True)
                    assert result.returncode == 1, result
                    assert 'data inspection failed' not in result.stdout, result.stdout
                    assert 'FAILED=1' in (root / 'output').read_text()
                # Valid bare/filename/uppercase sidecars reach the hard Windows inspection.
                for suffix in ['', '  ' + name, ' *' + name]:
                    sidecar.write_text(hashlib.sha256(b'not a ZIP').hexdigest().upper() + suffix)
                    result = subprocess.run([sys.executable, '-c', isolated], cwd=root, env=env, capture_output=True, text=True)
                    assert result.returncode == 1 and 'BadZipFile' in result.stdout, result
                (assets / 'extra.txt').write_text('extra')
                result = subprocess.run([sys.executable, '-c', isolated], cwd=root, env=env, capture_output=True, text=True)
                assert result.returncode != 0 and 'inventory' in result.stderr, result
                (assets / 'extra.txt').unlink()
                sidecar.unlink()
                result = subprocess.run([sys.executable, '-c', isolated], cwd=root, env=env, capture_output=True, text=True)
                assert result.returncode != 0 and 'inventory' in result.stderr, result
                # Correct Windows embedded version plus matching hash succeeds.
                with zipfile.ZipFile(assets / name, 'w') as z:
                    z.writestr('app/VPNRouter.Core.dll', '3.0.0'.encode('utf-16-le'))
                sidecar.write_text(hashlib.sha256((assets / name).read_bytes()).hexdigest())
                result = subprocess.run([sys.executable, '-c', isolated], cwd=root, env=env, capture_output=True, text=True)
                assert result.returncode == 0 and 'All checks passed' in result.stdout, result
                # Valid hash cannot hide a wrong embedded Windows version.
                with zipfile.ZipFile(assets / name, 'w') as z:
                    z.writestr('app/VPNRouter.Core.dll', '2.99.0'.encode('utf-16-le'))
                sidecar.write_text(hashlib.sha256((assets / name).read_bytes()).hexdigest())
                result = subprocess.run([sys.executable, '-c', isolated], cwd=root, env=env, capture_output=True, text=True)
                assert result.returncode == 1 and 'AppVersion mismatch' in result.stdout, result
            print('Benign workflow fixtures passed')
            """;
        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(fixture);
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var process = Process.Start(start)!;
        process.StandardInput.Write(python);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Benign Python fixtures timed out");
        }
        Assert.True(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static string Source()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "VPNRouter.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, ".github", "workflows", "verify-release-integrity.yml"));
    }

    private static YamlMappingNode Job()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(Source()));
        Assert.Single(yaml.Documents);
        var document = (YamlMappingNode)yaml.Documents[0].RootNode;
        return (YamlMappingNode)((YamlMappingNode)document.Children[new YamlScalarNode("jobs")]).Children[new YamlScalarNode("verify")];
    }

    private static List<YamlMappingNode> Steps() =>
        ((YamlSequenceNode)Job().Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();
    private static string Script(string name) => Text(Steps().Single(s => Text(s, "name") == name), "run");
    private static string Text(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? ((YamlScalarNode)node).Value ?? "" : "";
}
