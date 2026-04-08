using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace LatticeVeilInstaller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Check if another instance is already running
            var currentProcess = Process.GetCurrentProcess();
            var runningProcesses = Process.GetProcessesByName(currentProcess.ProcessName);
            
            // Check if there are other instances running (excluding current process)
            var otherInstances = runningProcesses.Where(p => p.Id != currentProcess.Id).ToList();
            
            if (otherInstances.Count > 0)
            {
                // Another instance is running, bring it to focus and exit immediately
                try
                {
                    var mainWindowHandle = otherInstances.First().MainWindowHandle;
                    if (mainWindowHandle != IntPtr.Zero)
                    {
                        // Show and bring the existing window to the foreground
                        ShowWindow(mainWindowHandle, 5); // SW_SHOW
                        SetForegroundWindow(mainWindowHandle);
                    }
                }
                catch
                {
                    // If we can't bring the other window to focus, just show a message
                }
                
                MessageBox.Show("LatticeVeil Installer is already running.", "Already Running", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            
            base.OnStartup(e);
            
            // Create and show main window
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
