using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LatticeVeilBuilder
{
    internal class Program
    {
        [STAThread]
        static void Main()
        {
            BuilderAppConfig.Initialize();
            Application.Run(new BuilderForm());
        }
    }
}
