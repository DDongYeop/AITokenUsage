using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Abstractions;

/// <summary>
/// Round-trips through the real Credential Manager using throw-away targets under
/// "AITokenUsage.Tests/&lt;guid&gt;/". Every entry is deleted in Dispose.
/// </summary>
public sealed partial class WindowsCredentialStoreTests : IDisposable
{
    private readonly string _prefix = $"AITokenUsage.Tests/{Guid.NewGuid():N}/";
    private readonly List<string> _created = [];
    private readonly WindowsCredentialStore _store = new();

    public void Dispose()
    {
        foreach (string target in _created)
        {
            Native.CredDelete(target, 1, 0);
        }
    }

    [Fact]
    public void ReadGeneric_DecodesUtf8Blob()
    {
        string target = _prefix + "main-account";
        WriteGeneric(target, "main-account", """{"token":{"accessToken":"test-access-token","note":"한글"}}""");

        Assert.Equal("""{"token":{"accessToken":"test-access-token","note":"한글"}}""", _store.ReadGeneric(target));
    }

    [Fact]
    public void ReadGeneric_ReturnsNull_WhenMissing()
    {
        Assert.Null(_store.ReadGeneric(_prefix + "missing"));
    }

    [Fact]
    public void EnumerateGeneric_ReturnsEveryMatch_WithUserNames()
    {
        WriteGeneric(_prefix + "https://github.com:alice", "https://github.com:alice", "gho_test-alice");
        WriteGeneric(_prefix + "https://github.com:bob", "https://github.com:bob", "gho_test-bob");

        var found = _store.EnumerateGeneric(_prefix + "*").OrderBy(c => c.TargetName, StringComparer.Ordinal).ToList();

        Assert.Equal(2, found.Count);
        Assert.Equal(_prefix + "https://github.com:alice", found[0].TargetName);
        Assert.Equal("https://github.com:alice", found[0].UserName);
        Assert.Equal("gho_test-alice", found[0].Secret);
        Assert.Equal("gho_test-bob", found[1].Secret);
    }

    [Fact]
    public void EnumerateGeneric_ReturnsEmpty_WhenNothingMatches()
    {
        Assert.Empty(_store.EnumerateGeneric(_prefix + "nothing-here*"));
    }

    [Fact]
    public void StoredCredential_ToString_DoesNotLeakSecret()
    {
        var credential = new StoredCredential("copilot-cli/https://github.com:alice", "alice", "gho_test-secret");

        Assert.DoesNotContain("gho_test-secret", credential.ToString());
    }

    private unsafe void WriteGeneric(string target, string userName, string secret)
    {
        byte[] blob = Encoding.UTF8.GetBytes(secret);
        IntPtr targetPtr = Marshal.StringToHGlobalUni(target);
        IntPtr userPtr = Marshal.StringToHGlobalUni(userName);
        try
        {
            fixed (byte* blobPtr = blob)
            {
                var credential = new Native.CREDENTIALW
                {
                    Type = 1, // CRED_TYPE_GENERIC
                    TargetName = targetPtr,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (IntPtr)blobPtr,
                    Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
                    UserName = userPtr,
                };

                if (!Native.CredWrite(in credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            _created.Add(target);
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
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

        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredWrite(in CREDENTIALW credential, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredDelete(string target, uint type, uint flags);
    }
}
