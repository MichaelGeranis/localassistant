using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LocalAssistant.Configuration
{
    /// <summary>
    /// Generic credentials in Windows Credential Manager (<c>CredReadW</c> / <c>CredWriteW</c>),
    /// called directly so the app keeps its zero-package rule. Windows encrypts them with the
    /// user's logon (DPAPI); they are visible under Control Panel → Credential Manager → Windows
    /// Credentials as <c>LocalAssistant/NAME</c>. The scheduled tasks run with an interactive logon,
    /// so they can read them.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsCredentialStore : ICredentialStore
    {
        private const uint CredTypeGeneric = 1;
        private const uint CredPersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;

        /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 bytes.</summary>
        private const int MaxBlobBytes = 5 * 512;

        public bool IsAvailable => true;

        public string? Read(string target)
        {
            if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNotFound)
                {
                    return null;
                }
                throw new Win32Exception(error, $"Could not read {target} from Credential Manager.");
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                return credential.CredentialBlobSize == 0
                    ? string.Empty
                    : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / sizeof(char));
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public void Write(string target, string secret)
        {
            var size = secret.Length * sizeof(char);
            if (size > MaxBlobBytes)
            {
                throw new ArgumentException($"A secret can be at most {MaxBlobBytes / sizeof(char)} characters.", nameof(secret));
            }

            var blob = Marshal.StringToCoTaskMemUni(secret);
            try
            {
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    CredentialBlob = blob,
                    CredentialBlobSize = (uint)size,
                    Persist = CredPersistLocalMachine,
                    UserName = Environment.UserName
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not write {target} to Credential Manager.");
                }
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUnicode(blob);
            }
        }

        /// <summary>Removes a credential; false when there was none.</summary>
        internal bool Delete(string target) => CredDelete(target, CredTypeGeneric, 0);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);
    }
}
