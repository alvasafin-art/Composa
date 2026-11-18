using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Composa.App.AI;

/// <summary>Credentials are local to the OS user and the configured service address.</summary>
internal static class UserApiKeys
{
    internal static string Target(string provider, Uri endpoint) =>
        "Composa/" + provider + "/" + endpoint.AbsoluteUri.TrimEnd('/');

    private const int NotFound = 1168;
    private const uint Generic = 1, LocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint Size;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr data);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential data, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr data);

    private static string UnixFile(string target) => Path.Combine(AppPaths.Config, "credentials",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target))));

    internal static string? Read(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            var path = UnixFile(target);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        if (!CredReadW(target, Generic, 0, out var data))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(data);
            return Marshal.PtrToStringUni(value.Blob, checked((int)value.Size / 2));
        }
        finally { CredFree(data); }
    }

    internal static void Write(string target, string? key)
    {
        if (!OperatingSystem.IsWindows())
        {
            var path = UnixFile(target);
            if (key == null) { if (File.Exists(path)) File.Delete(path); return; }
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temp = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            try
            {
                using (var stream = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                using (var writer = new StreamWriter(stream)) writer.Write(key);
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return;
        }
        if (key == null)
        {
            if (!CredDeleteW(target, Generic, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != NotFound) throw new Win32Exception(error);
            }
            return;
        }
        var blob = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var credential = new Credential { Type = Generic, TargetName = target, UserName = "Composa",
                Size = checked((uint)key.Length * 2), Blob = blob, Persist = LocalMachine };
            if (!CredWriteW(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
}
