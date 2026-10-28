using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>Read-only access to generic entries of Windows Credential Manager (advapi32 CredReadW / CredEnumerateW).</summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;

    public string? ReadGeneric(string targetName)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetName);

        if (!Native.CredRead(targetName, CredTypeGeneric, 0, out IntPtr credentialPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorNotFound or ErrorNoSuchLogonSession)
            {
                return null;
            }

            throw new Win32Exception(error, "CredReadW failed.");
        }

        try
        {
            Native.CREDENTIALW credential = Marshal.PtrToStructure<Native.CREDENTIALW>(credentialPtr);
            return ReadBlob(credential);
        }
        finally
        {
            Native.CredFree(credentialPtr);
        }
    }

    public IReadOnlyList<StoredCredential> EnumerateGeneric(string filter)
    {
        ArgumentException.ThrowIfNullOrEmpty(filter);

        if (!Native.CredEnumerate(filter, 0, out uint count, out IntPtr arrayPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorNotFound or ErrorNoSuchLogonSession)
            {
                return [];
            }

            throw new Win32Exception(error, "CredEnumerateW failed.");
        }

        try
        {
            var results = new List<StoredCredential>((int)count);
            for (int i = 0; i < count; i++)
            {
                // arrayPtr is PCREDENTIALW*: an array of pointers, one per credential.
                IntPtr itemPtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                Native.CREDENTIALW credential = Marshal.PtrToStructure<Native.CREDENTIALW>(itemPtr);
                if (credential.Type != CredTypeGeneric)
                {
                    continue;
                }

                string targetName = Marshal.PtrToStringUni(credential.TargetName) ?? "";
                string? userName = Marshal.PtrToStringUni(credential.UserName);
                results.Add(new StoredCredential(targetName, userName, ReadBlob(credential)));
            }

            return results;
        }
        finally
        {
            // One CredFree releases the pointer array and every credential it points to.
            Native.CredFree(arrayPtr);
        }
    }

    private static string ReadBlob(in Native.CREDENTIALW credential)
    {
        if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
        {
            return "";
        }

        byte[] bytes = new byte[credential.CredentialBlobSize];
        Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct CREDENTIALW
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public uint LastWrittenLow;
            public uint LastWrittenHigh;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentials);

        [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
        internal static partial void CredFree(IntPtr buffer);
    }
}
