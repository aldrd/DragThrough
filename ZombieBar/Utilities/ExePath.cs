using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ZombieBar.Utilities
{
    class ExePath
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);
        static readonly int MAX_PATH = 260;

        internal static string GetExecutablePath()
        {
            // Prefer the framework-provided path; it handles long paths and avoids native interop entirely.
            string processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                return processPath;
            }

            var sb = new StringBuilder(MAX_PATH);
            uint length = GetModuleFileName(IntPtr.Zero, sb, sb.Capacity);
            if (length == 0)
            {
                return string.Empty;
            }
            return sb.ToString();
        }
	}
}
