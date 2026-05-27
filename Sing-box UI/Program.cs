using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace Sing_box_UI
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Local\SingBoxUiTray";

        [STAThread]
        private static void Main()
        {
            if (!EnsureAdministrator())
            {
                return;
            }

            ReplaceRunningInstance();

            using (var singleInstanceMutex = new Mutex(false, SingleInstanceMutexName))
            {
                if (!TryAcquireSingleInstanceMutex(singleInstanceMutex))
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext());
            }
        }

        private static bool EnsureAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    return true;
                }
            }

            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Application.StartupPath
                }))
                {
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to request administrator rights:\r\n" + ex.Message,
                    "Sing-box UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return false;
        }

        private static void ReplaceRunningInstance()
        {
            var currentProcess = Process.GetCurrentProcess();
            var currentProcessId = currentProcess.Id;
            var currentProcessPath = NormalizePath(Application.ExecutablePath);
            var currentProcessName = Path.GetFileNameWithoutExtension(Application.ExecutablePath);

            foreach (var process in Process.GetProcessesByName(currentProcessName).Where(p => p.Id != currentProcessId))
            {
                using (process)
                {
                    try
                    {
                        if (!string.Equals(GetProcessPath(process), currentProcessPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        ForceKillProcessTree(process.Id);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool TryAcquireSingleInstanceMutex(Mutex singleInstanceMutex)
        {
            try
            {
                return singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(10), false);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        private static string GetProcessPath(Process process)
        {
            try
            {
                return NormalizePath(process.MainModule.FileName);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void ForceKillProcessTree(int processId)
        {
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/PID " + processId + " /F /T",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                if (process == null)
                {
                    return;
                }

                process.WaitForExit(5000);
            }
        }
    }
}
