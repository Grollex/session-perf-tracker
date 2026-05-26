using System;
using System.Diagnostics;
using System.IO;

namespace GeminiLauncher
{
    class Program
    {
        static void Main(string[] args)
        {
            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeminiAgent.ps1");
            
            if (!File.Exists(scriptPath))
            {
                Console.WriteLine("Error: GeminiAgent.ps1 not found in the same directory.");
                Console.ReadLine();
                return;
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\"", scriptPath);
            psi.UseShellExecute = false;
            
            try
            {
                Process p = Process.Start(psi);
                p.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to launch Gemini Agent: " + ex.Message);
                Console.ReadLine();
            }
        }
    }
}
