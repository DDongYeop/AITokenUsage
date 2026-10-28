using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// Stores secrets as "&lt;name&gt;.bin" files encrypted with DPAPI (CurrentUser scope).
/// Uses crypt32 directly because System.Security.Cryptography.ProtectedData is not part of
/// Microsoft.NETCore.App and third-party/extra NuGet packages are not allowed.
/// </summary>
public sealed partial class DpapiSecretStore : ISecretStore
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AITokenUsage.SecretStore.v1");

    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    public void Save(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        string path = GetPath(name);
        Directory.CreateDirectory(_directory);

        byte[] encrypted = Protect(Encoding.UTF8.GetBytes(secret));
        string tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, path, overwrite: true);
    }

    public string? Load(string name)
    {
        string path = GetPath(name);
        byte[] encrypted;
        try
        {
            // Shared read, no separate Exists check: Save/Delete may run concurrently.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            encrypted = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Unprotect(encrypted));
        }
        catch (CryptographicException)
        {
            // Corrupted file or encrypted by another Windows user: treat as missing.
            return null;
        }
    }

    public void Delete(string name)
    {
        string path = GetPath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            throw new ArgumentException("Secret name must be a plain file name.", nameof(name));
        }

        return Path.Combine(_directory, name + ".bin");
    }

    private static unsafe byte[] Protect(byte[] plain)
    {
        fixed (byte* plainPtr = plain)
        fixed (byte* entropyPtr = Entropy)
        {
            var input = new Native.DATA_BLOB { cbData = (uint)plain.Length, pbData = (IntPtr)plainPtr };
            var entropy = new Native.DATA_BLOB { cbData = (uint)Entropy.Length, pbData = (IntPtr)entropyPtr };
            if (!Native.CryptProtectData(in input, null, in entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out Native.DATA_BLOB output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return CopyAndFree(output);
        }
    }

    private static unsafe byte[] Unprotect(byte[] encrypted)
    {
        fixed (byte* encryptedPtr = encrypted)
        fixed (byte* entropyPtr = Entropy)
        {
            var input = new Native.DATA_BLOB { cbData = (uint)encrypted.Length, pbData = (IntPtr)encryptedPtr };
            var entropy = new Native.DATA_BLOB { cbData = (uint)Entropy.Length, pbData = (IntPtr)entropyPtr };
            if (!Native.CryptUnprotectData(in input, IntPtr.Zero, in entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out Native.DATA_BLOB output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return CopyAndFree(output);
        }
    }

    private static byte[] CopyAndFree(Native.DATA_BLOB blob)
    {
        try
        {
            byte[] result = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            // CryptProtectData/CryptUnprotectData allocate the output with LocalAlloc.
            Native.LocalFree(blob.pbData);
        }
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DATA_BLOB
        {
            public uint cbData;
            public IntPtr pbData;
        }

        [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CryptProtectData(
            in DATA_BLOB dataIn, string? description, in DATA_BLOB optionalEntropy,
            IntPtr reserved, IntPtr promptStruct, uint flags, out DATA_BLOB dataOut);

        [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CryptUnprotectData(
            in DATA_BLOB dataIn, IntPtr description, in DATA_BLOB optionalEntropy,
            IntPtr reserved, IntPtr promptStruct, uint flags, out DATA_BLOB dataOut);

        [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
        internal static partial IntPtr LocalFree(IntPtr memory);
    }
}
