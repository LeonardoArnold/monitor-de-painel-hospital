using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GHospPanelWatchdog.Native;

/// <summary>Leitura de linha de comando de processos e lançamento com controle de exibição.</summary>
internal static class ProcessNative
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const uint TH32CS_SNAPPROCESS = 0x2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    /// <summary>
    /// Lê a linha de comando de outro processo sem WMI (Windows 8.1+). Retorna null se não for possível.
    /// </summary>
    public static string? GetCommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int size = 4096;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out int needed);
                    if (status == 0)
                    {
                        // UNICODE_STRING { ushort Length; ushort MaxLength; IntPtr Buffer }
                        ushort len = (ushort)Marshal.ReadInt16(buf);
                        var strPtr = Marshal.ReadIntPtr(buf, IntPtr.Size);
                        return strPtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(strPtr, len / 2);
                    }
                    if (needed > size) { size = needed; continue; }
                    return null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 entry);

    /// <summary>PID raiz + todos os descendentes (via snapshot do ToolHelp, barato).</summary>
    public static HashSet<int> GetProcessTree(int rootPid)
    {
        var result = new HashSet<int> { rootPid };
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var parents = new List<(int pid, int parent)>();
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(snap, ref e))
            {
                do parents.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID));
                while (Process32NextW(snap, ref e));
            }
            bool added;
            do
            {
                added = false;
                foreach (var (pid, parent) in parents)
                    if (result.Contains(parent) && result.Add(pid)) added = true;
            } while (added);
        }
        finally { CloseHandle(snap); }
        return result;
    }

    // ---------- CreateProcess com wShowWindow (Process.Start não permite SW_SHOWNOACTIVATE) ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, System.Text.StringBuilder cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    private const int STARTF_USESHOWWINDOW = 0x1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;

    /// <summary>Inicia um processo pedindo que a primeira janela seja exibida sem ativar (sem roubar foco).</summary>
    public static int StartNoActivate(string exePath, string arguments, string workingDir)
    {
        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESHOWWINDOW,
            wShowWindow = Win32.SW_SHOWNOACTIVATE,
        };
        var cmd = new System.Text.StringBuilder($"\"{exePath}\" {arguments}");
        if (!CreateProcessW(exePath, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, workingDir, ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }
}
