using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AITokenUsage.Core.Models;

namespace AITokenUsage.UI;

public static class InstalledServiceIcons
{
    private sealed record Candidate(string Path, bool Packaged);
    private static readonly object Gate = new();
    private static readonly Lazy<Dictionary<string, List<Candidate>>> Entries = new(ReadAppsFolder);
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

    public static ImageSource? Load(string providerId)
    {
        if (string.IsNullOrEmpty(providerId)) return null;
        string kind = ProviderIds.KindOf(providerId);
        if (kind is not ("claude" or "codex" or "antigravity" or "gemini" or "copilot")) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(kind, out var image)) return image;
            if (Entries.Value.TryGetValue(kind, out var entries))
            {
                foreach (var entry in entries)
                {
                    image = entry.Packaged ? PackageIcon(entry.Path) : ExecutableIcon(entry.Path);
                    if (image is not null) break;
                }
            }
            if (image is null && kind == "antigravity")
            {
                string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
                image = ExecutableIcon(Path.Combine(programs, "Antigravity", "Antigravity.exe"));
            }
            return Cache[kind] = image;
        }
    }

    internal static string? PackageKind(string? appId) => appId?.ToLowerInvariant() switch
    {
        "openai.codex_2p2nqsd0c76g0!app" => "codex",
        "anthropicpbc.claude_fnn82j28hfe8t!claude" or "claude_pzs8sxrjxfjjc!claude" => "claude",
        _ => null,
    };

    internal static string? DisplayKind(string? name) => name?.ToLowerInvariant() switch
    {
        "codex" or "openai codex" => "codex",
        "claude" => "claude",
        "antigravity" or "google antigravity" or "antigravity ide" => "antigravity",
        "gemini" or "google gemini" => "gemini",
        "github copilot" => "copilot",
        _ => null,
    };

    internal static bool MatchesExecutable(string kind, string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return false;
        string target = Path.GetFullPath(path);
        return !target.StartsWith(@"\\", StringComparison.Ordinal) &&
            !target.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase) &&
            !target.Contains(@"\shims\", StringComparison.OrdinalIgnoreCase) &&
            !target.Contains(@"\WinGet\Links\", StringComparison.OrdinalIgnoreCase) &&
            !target.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase) &&
            DisplayKind(Path.GetFileNameWithoutExtension(target)) == kind &&
            string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    // AppsFolder includes registered desktop Start menu entries as well as packaged applications.
    // Keep only matching identities/targets, never the Shell automation objects.
    private static Dictionary<string, List<Candidate>> ReadAppsFolder()
    {
        Dictionary<string, List<Candidate>> result = new();
        dynamic? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            folder = shell!.NameSpace("shell:AppsFolder");
            if (folder is null) return result;
            items = folder.Items();
            int count = items.Count;
            for (int index = 0; index < count; index++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(index);
                    if (item is null) continue;
                    string? id = item.Path;
                    string? kind = PackageKind(id);
                    bool packaged = kind is not null;
                    if (!packaged)
                    {
                        kind = DisplayKind((string?)item.Name);
                        if (kind is null) continue;
                        // A packaged entry has a package family and an application ID. Its target may be opaque.
                        packaged = IsPackageId(id);
                        if (!packaged)
                        {
                            id = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                            if (!MatchesExecutable(kind, id)) continue;
                        }
                    }
                    if (!result.TryGetValue(kind!, out var candidates)) result[kind!] = candidates = [];
                    var candidate = new Candidate(id!, packaged);
                    if (packaged) candidates.Insert(0, candidate);
                    else candidates.Add(candidate);
                }
                catch (Exception ex) when (ShellFailure(ex)) { }
                finally { Release((object?)item); }
            }
        }
        catch (Exception ex) when (ShellFailure(ex)) { }
        finally
        {
            Release((object?)items);
            Release((object?)folder);
            Release((object?)shell);
        }
        return result;
    }

    internal static bool IsPackageId(string? id) => id is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[A-Za-z0-9.-]+_[a-z0-9]{13}![A-Za-z0-9.-]+\z");

    private static void Release(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
    }

    private static bool ShellFailure(Exception ex) => ex is COMException or UnauthorizedAccessException
        or IOException or ArgumentException or NotSupportedException or InvalidOperationException
        or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    private static ImageSource? ExecutableIcon(string path)
    {
        IntPtr icon = IntPtr.Zero;
        try
        {
            if (!Path.IsPathFullyQualified(path) || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed ||
                !File.Exists(path)) return null;
            // Extract only an embedded icon resource. Unlike associated-icon lookup this has no file-type fallback.
            ExtractIconEx(path, 0, out icon, IntPtr.Zero, 1);
            if (icon == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ShellFailure(ex)) { return null; }
        finally { if (icon != IntPtr.Zero) DestroyIcon(icon); }
    }

    private static ImageSource? PackageIcon(string appId)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref iid, out factory) < 0 ||
                factory is null || factory.GetImage(new NativeSize(64, 64), 4 /* SIIGBF_ICONONLY */, out bitmap) < 0 ||
                bitmap == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ShellFailure(ex)) { return null; }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Release(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string name, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, IntPtr small, uint count);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr bitmap);
}
