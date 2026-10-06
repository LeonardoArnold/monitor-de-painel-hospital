using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace GHospPanelWatchdog.Security;

internal sealed record PanelCredential(string UserName, string Password);

/// <summary>
/// Credenciais do G-HOSP no Gerenciador de Credenciais do Windows (protegidas por DPAPI,
/// vinculadas à conta do Windows). Nada é gravado em arquivo de configuração ou log.
/// </summary>
internal static class CredentialStore
{
    private const string Target = "GHospPanelWatchdog/GHosp";
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static bool HasCredential()
    {
        if (!CredRead(Target, CRED_TYPE_GENERIC, 0, out var p)) return false;
        CredFree(p);
        return true;
    }

    public static string? ReadUserName()
    {
        if (!CredRead(Target, CRED_TYPE_GENERIC, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            return c.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(c.UserName);
        }
        finally { CredFree(p); }
    }

    public static PanelCredential? Read()
    {
        if (!CredRead(Target, CRED_TYPE_GENERIC, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            var user = c.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(c.UserName) ?? "";
            var pass = c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
            return new PanelCredential(user, pass);
        }
        finally { CredFree(p); }
    }

    public static void Write(string userName, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var blobPtr = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
        var targetPtr = Marshal.StringToHGlobalUni(Target);
        var userPtr = Marshal.StringToHGlobalUni(userName);
        var commentPtr = Marshal.StringToHGlobalUni("G-HOSP Panel Watchdog — login do painel");
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                Comment = commentPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPtr,
            };
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível gravar a credencial no Gerenciador de Credenciais.");
        }
        finally
        {
            // Zera as cópias da senha em memória não gerenciada e no array.
            for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
            Array.Clear(blob);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
            Marshal.FreeHGlobal(commentPtr);
        }
    }

    public static void Delete()
    {
        if (!CredDelete(Target, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
        }
    }
}
