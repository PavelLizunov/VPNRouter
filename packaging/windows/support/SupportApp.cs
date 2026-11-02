// Built with the Windows .NET Framework compiler; no .NET 10 installation needed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VPNRouter.Business.Support
{
    public interface ISupportWorkflow { void Send(Action<string> update); void Cancel(); }

    public sealed class SupportWindow : Form
    {
        private readonly Button send = new Button();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly ISupportWorkflow runner;
        private bool busy;
        private bool closeRequested;

        public SupportWindow(ISupportWorkflow workflow)
        {
            runner = workflow;
            Text = "VPNRouter Business - Диагностика";
            ClientSize = new Size(470, 260);
            MinimumSize = Size;
            MaximumSize = Size;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 10);
            var description = new Label();
            description.Text = "Отправьте администратору журналы VPNRouter, чтобы разобраться с запуском или соединением.\r\n\r\nПароли, ключи и ссылки маскируются. В отчёте могут остаться домены, IP и имена приложений.";
            description.SetBounds(22, 18, 426, 128);
            status.SetBounds(22, 152, 426, 42);
            status.Text = "Готово к сбору и отправке.";
            progress.SetBounds(22, 205, 190, 23);
            progress.Style = ProgressBarStyle.Marquee;
            progress.Visible = false;
            send.SetBounds(230, 200, 218, 36);
            send.Text = "Отправить диагностику";
            send.AccessibleName = send.Text;
            send.Click += SendClicked;
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!busy) return;
                e.Cancel = true;
                closeRequested = true;
                SetStatus("Останавливаем отправку…");
                if (runner != null) runner.Cancel();
            };
            Controls.AddRange(new Control[] { description, status, progress, send });
            AcceptButton = send;
        }

        private async void SendClicked(object sender, EventArgs e)
        {
            send.Enabled = false;
            busy = true;
            progress.Visible = true;
            try
            {
                if (runner == null) throw new InvalidOperationException();
                await Task.Run(delegate { runner.Send(SetStatus); });
                if (closeRequested) return;
                SetStatus("Диагностика доставлена администратору.\r\nЭто окно можно закрыть.");
                send.Text = "Отправлено";
            }
            catch
            {
                if (closeRequested) return;
                SetStatus("Не удалось подтвердить отправку.\r\nПроверьте интернет и нажмите «Повторить».");
                send.Text = "Повторить";
                send.Enabled = true;
            }
            finally
            {
                busy = false;
                if (!IsDisposed) progress.Visible = false;
                if (closeRequested && !IsDisposed) Close();
            }
        }

        private void SetStatus(string text)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(SetStatus), text); }
                catch (InvalidOperationException) { }
                return;
            }
            status.Text = text;
        }
    }

    public sealed class SupportRunner : ISupportWorkflow
    {
        private readonly string endpoint;
        private readonly string token;
        private readonly string reportDirectory;
        private string report;
        private readonly object processLock = new object();
        private Process currentProcess;
        private bool cancelled;

        public SupportRunner(string target, string credential, string folder)
        {
            Uri uri;
            if (!Uri.TryCreate(target, UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Invalid HTTPS endpoint.");
            endpoint = uri.AbsoluteUri;
            if (String.IsNullOrWhiteSpace(credential)) throw new ArgumentException("Upload identity missing.");
            token = credential;
            reportDirectory = folder;
        }

        public void Send(Action<string> update)
        {
            CreatePrivateDirectory(reportDirectory);
            var stage = CreatePrivateStage();
            try
            {
                var collect = WriteResource(stage, "Collect-VPNRouterBusiness-Diagnostics.ps1");
                var upload = WriteResource(stage, "Send-VPNRouterBusiness-Diagnostics.ps1");
                var tokenPath = Path.Combine(stage, "upload-token.txt");
                File.WriteAllText(tokenPath, token, new System.Text.UTF8Encoding(false));
                if (report == null || !File.Exists(report))
                {
                    update("Собираем диагностику…");
                    // A unique folder also prevents selecting another instance's report.
                    var session = Path.Combine(reportDirectory, Guid.NewGuid().ToString("N"));
                    CreatePrivateDirectory(session);
                    RunPowerShell(collect, "-DestinationDir " + Quote(session) + " -NoOpen");
                    var files = Directory.GetFiles(session, "VPNRouterBusiness-diagnostics-*.zip");
                    if (files.Length != 1) throw new IOException("Report missing.");
                    report = files[0];
                }
                update("Отправляем отчёт администратору…");
                RunPowerShell(upload, "-ZipPath " + Quote(report) + " -UploadEndpoint " + Quote(endpoint) + " -TokenFile " + Quote(tokenPath));
            }
            finally
            {
                try { Directory.Delete(stage, true); } catch { }
            }
        }

        public void Cancel()
        {
            lock (processLock)
            {
                cancelled = true;
                try { if (currentProcess != null && !currentProcess.HasExited) currentProcess.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        public static string CreatePrivateStage()
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VPNRouterBusinessSupport", "Staging", Guid.NewGuid().ToString("N"));
            CreatePrivateDirectory(directory);
            return directory;
        }

        private static void CreatePrivateDirectory(string directory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            SecurityIdentifier user;
            using (var identity = WindowsIdentity.GetCurrent()) user = identity.User;
            foreach (var sid in new[] { user,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(directory, security);
            // Refuse a pre-existing/replaced link before writing executable resources.
            var item = new DirectoryInfo(directory);
            for (var parent = item; parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked support staging refused.");
            item.SetAccessControl(security);
        }

        private static string WriteResource(string folder, string name)
        {
            var path = Path.Combine(folder, name);
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (input == null) throw new IOException("Support resource missing.");
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
            }
            return path;
        }

        private static string Quote(string argument)
        {
            if (argument.IndexOf('"') >= 0 || argument.IndexOf('\r') >= 0 || argument.IndexOf('\n') >= 0)
                throw new ArgumentException("Invalid argument.");
            return "\"" + argument.TrimEnd('\\') + "\"";
        }

        private void RunPowerShell(string script, string arguments)
        {
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(shell,
                "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(script) + " " + arguments);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            // The scripts print generic status only. Do not forward raw process errors.
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            Process process;
            lock (processLock)
            {
                if (cancelled) throw new OperationCanceledException();
                process = Process.Start(start);
                currentProcess = process;
            }
            try
            {
                process.OutputDataReceived += delegate { };
                process.ErrorDataReceived += delegate { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(90000))
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
                    throw new TimeoutException();
                }
                if (process.ExitCode != 0) throw new IOException("Support operation failed.");
            }
            finally
            {
                lock (processLock) { currentProcess = null; process.Dispose(); }
            }
        }
    }

    public static class SupportProgram
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string json;
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("support-settings.json"))
                using (var reader = new StreamReader(stream)) json = reader.ReadToEnd();
                var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
                var reports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VPNRouterBusinessSupport", "Reports");
                Application.Run(new SupportWindow(new SupportRunner(settings["endpoint"], settings["token"], reports)));
            }
            catch
            {
                MessageBox.Show("Не удалось запустить диагностику. Обратитесь к администратору за новым файлом.",
                    "VPNRouter Business", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
