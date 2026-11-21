using VPNRouter.Core.Platform.macOS;
using Xunit;

namespace VPNRouter.Tests;

public class MacPrivilegedSourceContractTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VPNRouter.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void Install_guide_has_no_manual_privilege_grant_or_terminal_command()
    {
        var guide = Read("VPNRouter.App", "Assets", "InstallGuide.html");

        Assert.DoesNotContain("sudo", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOPASSWD", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("pkill", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc/sudoers", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("sing-box", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("v2.0.0", guide, StringComparison.Ordinal);
        Assert.Contains("Connect", guide, StringComparison.Ordinal);
        Assert.Contains("one-time", guide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void App_code_has_no_direct_sudoers_writer()
    {
        var appDir = Path.Combine(RepoRoot(), "VPNRouter.App");
        var coreDir = Path.Combine(RepoRoot(), "VPNRouter.Core", "Platform", "macOS");
        var offenders = Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith("MacPrivilegePolicy.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("NOPASSWD", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Mac_sudo_entry_point_delegates_to_managed_setup()
    {
        var source = Read("VPNRouter.App", "ViewModels", "MainWindowViewModel.MacSudo.cs");

        Assert.Contains("MacPrivilegedSetup.EnsureInstalled", source, StringComparison.Ordinal);
        Assert.DoesNotContain("osascript", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("macos-sudoers.marker", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Mac_launch_path_uses_the_managed_helper_not_a_user_sing_box()
    {
        var lifecycle = Read("VPNRouter.Core", "Services", "SingBoxManager.Lifecycle.cs");

        Assert.Contains("new MacPrivilegedClient", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("spawnExe = \"/usr/bin/sudo\"", lifecycle, StringComparison.Ordinal);
        Assert.Contains("CreateStartRequest(_currentConfigPath)", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_script_compiles_helper_and_removes_terminal_step()
    {
        var build = Read("build-mac.sh");

        Assert.Contains("vpnrouter-privileged.c", build, StringComparison.Ordinal);
        Assert.Contains("clang -std=c11", build, StringComparison.Ordinal);
        Assert.DoesNotContain("/Contents/MacOS/install-privileged.sh", build, StringComparison.Ordinal);
        Assert.DoesNotContain("Terminal.app", build, StringComparison.Ordinal);

        var workflow = Read(".github", "workflows", "build-mac.yml");
        Assert.Contains("packaging/macos/tests/run-tests.sh", workflow, StringComparison.Ordinal);

        var prWorkflow = Read(".github", "workflows", "macos-privileged.yml");
        Assert.Contains("pull_request", prWorkflow, StringComparison.Ordinal);
        Assert.Contains("packaging/macos/tests/run-tests.sh", prWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("upload-artifact", prWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("tags:", prWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_installer_matches_the_source_and_is_frozen_in_the_command()
    {
        var script = MacPrivilegedSetup.ReadInstallerScript();
        Assert.Equal(
            Read("packaging", "macos", "install-privileged.sh").Replace("\r\n", "\n", StringComparison.Ordinal),
            script.Replace("\r\n", "\n", StringComparison.Ordinal));

        var command = MacPrivilegedSetup.BuildInstallerCommand(script, MacPrivilegePolicy.Render("alice"),
            "/Applications/fixture/helper", "/Applications/fixture/sing-box",
            new string('a', 64), new string('b', 64), "2.51.0-r1", "alice");
        var payload = command.Split('\'')[1];
        Assert.Equal(script, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
    }

    [Fact]
    public void Start_and_stop_serialize_state_before_staging_or_signaling()
    {
        var helper = Read("packaging", "macos", "vpnrouter-privileged.c");

        var start = helper.IndexOf("static int command_start", StringComparison.Ordinal);
        var stateLock = helper.IndexOf("acquire_state_lock(uid)", start, StringComparison.Ordinal);
        var lease = helper.IndexOf("acquire_session_lease(uid)", stateLock, StringComparison.Ordinal);
        var assets = helper.IndexOf("sync_assets(uid, home)", lease, StringComparison.Ordinal);
        var staged = helper.IndexOf("write_root_file(staged", assets, StringComparison.Ordinal);
        var record = helper.IndexOf("write_session_record(uid, getpid()", staged, StringComparison.Ordinal);
        var exec = helper.IndexOf("execve(ROOT_BIN", record, StringComparison.Ordinal);
        Assert.True(start >= 0 && stateLock > start && lease > stateLock &&
                    assets > lease && staged > assets && record > staged && exec > record,
            "start must hold both locks before syncing assets, staging config or publishing ownership");

        var stop = helper.IndexOf("static int command_stop", StringComparison.Ordinal);
        var stopLock = helper.IndexOf("acquire_state_lock(uid)", stop, StringComparison.Ordinal);
        var stopSignal = helper.IndexOf("kill(pid, SIGKILL)", stopLock, StringComparison.Ordinal);
        Assert.True(stop >= 0 && stopLock > stop && stopSignal > stopLock,
            "stop must serialize state before the exact signal");
    }

    [Fact]
    public void Helper_source_uses_typed_validation_and_no_shell()
    {
        var source = Read("packaging", "macos", "vpnrouter-privileged.c");

        Assert.Contains("SUDO_UID", source, StringComparison.Ordinal);
        Assert.Contains("O_NOFOLLOW", source, StringComparison.Ordinal);
        Assert.Contains("validate_config", source, StringComparison.Ordinal);
        Assert.Contains("validate_unique_keys", source, StringComparison.Ordinal);
        Assert.Contains("key_token_is_canonical", source, StringComparison.Ordinal);
        Assert.Contains("validate_experimental", source, StringComparison.Ordinal);
        Assert.Contains("validate_inbounds", source, StringComparison.Ordinal);
        Assert.Contains("validate_rule_sets", source, StringComparison.Ordinal);
        Assert.Contains("is_root_asset_path", source, StringComparison.Ordinal);
        Assert.Contains("validate_pf_rules", source, StringComparison.Ordinal);
        Assert.Contains("execve(ROOT_BIN", source, StringComparison.Ordinal);
        Assert.Contains("int main(void)", source, StringComparison.Ordinal);
        Assert.Contains("legacy_policy_state_path", source, StringComparison.Ordinal);
        Assert.Contains("LEGACY_UNKNOWN", source, StringComparison.Ordinal);
        Assert.Contains("verify_fixed_chain", source, StringComparison.Ordinal);
        Assert.Contains("verify_runtime_artifact", source, StringComparison.Ordinal);
        Assert.Contains("read_session_record", source, StringComparison.Ordinal);
        Assert.Contains("write_session_record", source, StringComparison.Ordinal);
        Assert.Contains("process_is_root_runtime", source, StringComparison.Ordinal);
        Assert.Contains("\"secret\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("system(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("popen(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("execvp", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sh -c", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_contracts_validate_canonical_keys_before_other_checks()
    {
        var helper = Read("packaging", "macos", "vpnrouter-privileged.c");
        var contract = Read("VPNRouter.Core", "Platform", "macOS", "MacPrivilegedConfigContract.cs");

        var validateConfig = helper.IndexOf("static int validate_config", StringComparison.Ordinal);
        Assert.True(validateConfig >= 0, "validate_config is missing");
        var keyCheck = helper.IndexOf("validate_unique_keys(doc, 0)", validateConfig, StringComparison.Ordinal);
        var logCheck = helper.IndexOf("validate_log(doc, 0)", validateConfig, StringComparison.Ordinal);
        Assert.True(keyCheck > validateConfig, "validate_config does not validate keys");
        Assert.True(logCheck > keyCheck, "key validation must run before value validation");

        Assert.Contains("key_token_is_canonical", helper, StringComparison.Ordinal);
        Assert.Contains("validate_unique_keys(&doc, 0)", helper, StringComparison.Ordinal);
        Assert.Contains("reserved_structural_keys", helper, StringComparison.Ordinal);
        Assert.Contains("ReservedStructuralKeys", contract, StringComparison.Ordinal);
        Assert.Contains("HasNoncanonicalStructuralCasing", contract, StringComparison.Ordinal);
        Assert.Contains("ValueIsEscaped", contract, StringComparison.Ordinal);
        Assert.Contains(
            "Privileged sing-box config keys must be canonical printable ASCII without escapes.",
            contract,
            StringComparison.Ordinal);

        var parseDoc = helper.IndexOf("static int parse_doc", StringComparison.Ordinal);
        Assert.True(parseDoc >= 0, "parse_doc is missing");
        var syntaxCheck = helper.IndexOf("json_syntax_is_valid(json, length)", parseDoc, StringComparison.Ordinal);
        var tokenize = helper.IndexOf("jsmn_parse(&parser", parseDoc, StringComparison.Ordinal);
        Assert.True(syntaxCheck > parseDoc && tokenize > syntaxCheck,
            "strict syntax and depth validation must run before tokenization");
    }

    [Fact]
    public void Policy_template_is_the_single_rendered_rule()
    {
        var template = Read("packaging", "macos", "sudoers.template");
        var expected = MacPrivilegePolicy.DefaultTemplate.Replace("\r\n", "\n", StringComparison.Ordinal);
        var actual = template.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(expected, actual);
        Assert.Single(template.Split('\n')
            .Where(line => line.Contains("ALL=(root) NOPASSWD:", StringComparison.Ordinal)));
        Assert.Contains("{{USER}}", template, StringComparison.Ordinal);
        Assert.DoesNotContain("*", template, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", template, StringComparison.Ordinal);
        Assert.DoesNotContain("sing-box", template, StringComparison.Ordinal);
        Assert.DoesNotContain("pfctl", template, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_validates_the_policy_bytes_bound_to_the_request()
    {
        var script = Read("packaging", "macos", "install-privileged.sh");

        Assert.Contains("--policy-base64", script, StringComparison.Ordinal);
        Assert.Contains("base64 -D > \"$policy_tmp\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--policy-template", script, StringComparison.Ordinal);
        Assert.Contains("visudo -cf", script, StringComparison.Ordinal);
        Assert.Contains("chmod 0440", script, StringComparison.Ordinal);
        Assert.Contains("rm -f \"$LEGACY_POLICY\"", script, StringComparison.Ordinal);
        Assert.Contains("check_root_dir", script, StringComparison.Ordinal);
        Assert.Contains("check_source_chain", script, StringComparison.Ordinal);
        Assert.Contains("expected_rule", script, StringComparison.Ordinal);
        Assert.Contains("legacy policy is still present", script, StringComparison.Ordinal);
        Assert.Contains("SUDOERS_DIR=\"/private/etc/sudoers.d\"", script, StringComparison.Ordinal);
        Assert.Contains("check_root_dir \"/Library/Application Support\" 0755 -", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("check_root_dir /etc ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("eval ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("curl ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("wget ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("echo \"$user_name ALL=(root) NOPASSWD:", script, StringComparison.Ordinal);

        var lockIndex = script.IndexOf("flock(STDIN, LOCK_EX | LOCK_NB)", StringComparison.Ordinal);
        var cleanupTrapIndex = script.IndexOf("trap cleanup EXIT", StringComparison.Ordinal);
        Assert.True(lockIndex >= 0 && cleanupTrapIndex > lockIndex,
            "Only the installer holding the lock may clean shared staging paths.");
    }

    [Fact]
    public void Installer_checks_legacy_runtime_and_artifacts_before_publishing_the_rule()
    {
        var script = Read("packaging", "macos", "install-privileged.sh");
        const string migrationCheck = "check_legacy_runtime\n";
        var firstCheck = script.IndexOf(migrationCheck, StringComparison.Ordinal);
        var legacyRemoval = script.IndexOf("rm -f \"$LEGACY_POLICY\"", StringComparison.Ordinal);
        var secondCheck = script.LastIndexOf(migrationCheck, StringComparison.Ordinal);
        var replaceHelper = script.IndexOf("mv -f \"$helper_tmp\" \"$HELPER_DEST\"", StringComparison.Ordinal);
        var publishRule = script.IndexOf("mv -f \"$policy_tmp\" \"$policy_path\"", StringComparison.Ordinal);
        Assert.True(firstCheck >= 0 && legacyRemoval > firstCheck && secondCheck > legacyRemoval &&
                    replaceHelper > secondCheck && publishRule > replaceHelper,
            "The installer must check for the old runtime around legacy rule removal.");

        foreach (var path in new[] { "HELPER_DEST", "ROOT_BIN", "MANIFEST" })
        {
            var verify = script.IndexOf($"verify_root_artifact \"${path}\"", StringComparison.Ordinal);
            Assert.True(verify > replaceHelper && verify < publishRule,
                $"Installed file {path} must be checked before publishing the rule.");
        }

        var armRollback = script.IndexOf("policy_pending=1", StringComparison.Ordinal);
        var probe = script.IndexOf("probe_output=", StringComparison.Ordinal);
        var commit = script.LastIndexOf("policy_pending=0", StringComparison.Ordinal);
        Assert.True(armRollback > replaceHelper && armRollback < publishRule && publishRule < probe && probe < commit,
            "Rollback stays armed until the installed helper reports readiness.");
        Assert.Contains("[ \"$policy_pending\" -eq 1 ] && [ ! -e \"$policy_tmp\" ] && ! rm -f \"$policy_path\"", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_validates_the_real_system_directory_chain()
    {
        var helper = Read("packaging", "macos", "vpnrouter-privileged.c");

        Assert.Contains("#define SUDOERS_DIR \"/private/etc/sudoers.d\"", helper,
            StringComparison.Ordinal);
        Assert.Contains("verify_root_dir(\"/Library/Application Support\", 0755, 0)", helper,
            StringComparison.Ordinal);
        Assert.Contains("verify_root_dir(\"/private/etc\", 0755, 1)", helper, StringComparison.Ordinal);
        Assert.Contains("verify_etc_alias() == 0", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("verify_root_dir(\"/etc\",", helper, StringComparison.Ordinal);
    }
}
