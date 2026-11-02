using System.Diagnostics;
using System.Text.Json;

namespace VPNRouter.Tests;

public sealed class BusinessSupportExeTests
{
    [Fact]
    public async Task SingleExe_EmbedsScriptsAndExercisesSuccessFailureAndCancellationWithoutDesktop()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRoot();
        var temp = Path.Combine(Path.GetTempPath(), "business-support-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var settings = Path.Combine(temp, "fixture-settings.json");
            File.WriteAllText(settings, JsonSerializer.Serialize(new
            {
                endpoint = "https://example.invalid/api/reports",
                token = "synthetic-fixture-upload-identity-not-a-live-token"
            }));
            var script = Path.Combine(temp, "verify.ps1");
            File.WriteAllText(script, """"
                $ErrorActionPreference = 'Stop'
                & $env:SUPPORT_BUILD -SettingsFile $env:SUPPORT_SETTINGS -OutputDir $env:SUPPORT_OUTPUT
                if ($LASTEXITCODE -ne 0) { throw 'Compiler failed.' }
                $exe = Join-Path $env:SUPPORT_OUTPUT 'VPNRouterBusiness-Diagnostics.exe'
                Add-Type -AssemblyName System.Windows.Forms,System.Drawing
                $assembly = [Reflection.Assembly]::LoadFile($exe)
                $resources = @($assembly.GetManifestResourceNames())
                if ($resources.Count -ne 3 -or $resources -notcontains 'support-settings.json' -or $resources -notcontains 'Collect-VPNRouterBusiness-Diagnostics.ps1' -or $resources -notcontains 'Send-VPNRouterBusiness-Diagnostics.ps1') { throw 'Missing embedded resources.' }
                foreach ($name in @('Collect-VPNRouterBusiness-Diagnostics.ps1','Send-VPNRouterBusiness-Diagnostics.ps1')) {
                    $reader = [IO.StreamReader]::new($assembly.GetManifestResourceStream($name))
                    try { $embedded = $reader.ReadToEnd().Replace("`r`n", "`n") } finally { $reader.Dispose() }
                    $expected = [IO.File]::ReadAllText((Join-Path $env:SUPPORT_SOURCE $name)).Replace("`r`n", "`n")
                    if ($expected -cne $embedded) { throw 'Collector changed during embedding.' }
                }
                $stage = [VPNRouter.Business.Support.SupportRunner]::CreatePrivateStage()
                try {
                    $acl = Get-Acl -LiteralPath $stage
                    if (!$acl.AreAccessRulesProtected) { throw 'Staging inherits permissions.' }
                    $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')
                    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
                        if ($rule.AccessControlType -eq 'Allow' -and $allowed -notcontains $rule.IdentityReference.Value) { throw 'Staging is accessible to another identity.' }
                    }
                } finally { Remove-Item -LiteralPath $stage -Recurse -Force }
                Add-Type -ReferencedAssemblies $exe,System.Windows.Forms,System.Drawing -TypeDefinition @'
                using System;
                using System.Threading;
                using VPNRouter.Business.Support;
                public sealed class FixtureWorkflow : ISupportWorkflow {
                    public bool Fail, WaitForCancel;
                    public volatile bool Cancelled;
                    public int Calls;
                    public void Send(Action<string> update) {
                        Calls++;
                        update("fixture progress");
                        if (WaitForCancel) {
                            var end = DateTime.UtcNow.AddSeconds(5);
                            while (!Cancelled && DateTime.UtcNow < end) Thread.Sleep(5);
                            if (!Cancelled) throw new Exception("cancel not observed");
                        }
                        if (Fail || Cancelled) throw new Exception("synthetic failure");
                    }
                    public void Cancel() { Cancelled = true; }
                }
                '@
                [Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Forms.WindowsFormsSynchronizationContext]::new())
                $click = [Windows.Forms.Button].GetMethod('OnClick', [Reflection.BindingFlags]'Instance,NonPublic')
                foreach ($mode in @('success','failure','cancel')) {
                    $workflow = New-Object FixtureWorkflow
                    $workflow.Fail = $mode -eq 'failure'
                    $workflow.WaitForCancel = $mode -eq 'cancel'
                    $form = [VPNRouter.Business.Support.SupportWindow]::new($workflow)
                    try {
                        # Create only offscreen control handles; never Show or capture a desktop.
                        $null = $form.Handle
                        foreach ($control in $form.Controls) { $null = $control.Handle }
                        $buttons = @($form.Controls | Where-Object { $_ -is [Windows.Forms.Button] })
                        if ($buttons.Count -ne 1 -or $buttons[0].Text -ne 'Отправить диагностику') { throw 'Employee must have one clear action.' }
                        $button = $buttons[0]
                        if ($mode -eq 'success') {
                            $bitmap = [Drawing.Bitmap]::new($form.Width, $form.Height)
                            try { $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0,0,$form.Width,$form.Height)); $bitmap.Save($env:SUPPORT_SCREENSHOT) } finally { $bitmap.Dispose() }
                        }
                        $click.Invoke($button, @([EventArgs]::Empty)) | Out-Null
                        if ($mode -eq 'cancel') { $form.Close() }
                        $end = [DateTime]::UtcNow.AddSeconds(8)
                        do {
                            [Windows.Forms.Application]::DoEvents()
                            Start-Sleep -Milliseconds 10
                        } while ([DateTime]::UtcNow -lt $end -and !$form.IsDisposed -and !$button.Enabled -and $button.Text -ne 'Отправлено')
                        if ($workflow.Calls -ne 1) { throw 'Repeated send.' }
                        if ($mode -eq 'success' -and ($button.Text -ne 'Отправлено' -or $button.Enabled)) { throw 'Success not visible.' }
                        if ($mode -eq 'failure' -and ($button.Text -ne 'Повторить' -or !$button.Enabled)) { throw 'Retry not available.' }
                        if ($mode -eq 'cancel' -and (!$workflow.Cancelled -or !$form.IsDisposed)) { throw 'Close did not stop the workflow.' }
                    } finally { $form.Dispose() }
                }
                Write-Output 'single EXE headless workflow checks passed'
                """", new System.Text.UTF8Encoding(true));
            var screenshot = Path.Combine(root, "VPNRouter.Tests", "screenshots", "business-support-send.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var arg in new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(arg);
            start.Environment["SUPPORT_BUILD"] = Path.Combine(root, "packaging", "windows", "support", "build-support.ps1");
            start.Environment["SUPPORT_SOURCE"] = Path.Combine(root, "packaging", "windows", "support");
            start.Environment["SUPPORT_SETTINGS"] = settings;
            start.Environment["SUPPORT_OUTPUT"] = Path.Combine(temp, "build");
            start.Environment["SUPPORT_SCREENSHOT"] = screenshot;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("single EXE headless workflow checks passed", output);
            Assert.True(File.Exists(screenshot));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VPNRouter.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
