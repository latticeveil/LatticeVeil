using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace LatticeVeilUninstaller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            var currentProcess = Process.GetCurrentProcess();
            var runningProcesses = Process.GetProcessesByName(currentProcess.ProcessName);

            var otherInstances = runningProcesses.Where(p => p.Id != currentProcess.Id).ToList();

            if (otherInstances.Count > 0)
            {
                try
                {
                    var mainWindowHandle = otherInstances.First().MainWindowHandle;
                    if (mainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(mainWindowHandle, 5);
                        SetForegroundWindow(mainWindowHandle);
                    }
                }
                catch
                {
                }

                MessageBox.Show("LatticeVeil Uninstaller is already running.", "Already Running",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
