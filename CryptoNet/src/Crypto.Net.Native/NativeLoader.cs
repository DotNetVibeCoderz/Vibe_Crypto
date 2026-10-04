using System.Reflection;
using System.Runtime.InteropServices;

namespace Crypto.Net.Native;

/// <summary>
/// Locates and loads the native <c>cryptonet</c> library and checks its ABI version.
/// </summary>
public static class NativeLoader
{
    private const string LibraryName = "cryptonet";

    /// <summary>Minimum ABI version (0xMMmmpppp) this assembly requires.</summary>
    public const uint RequiredAbiVersion = 0x00010100;

    private static readonly Lazy<bool> _isAvailable = new(Initialize, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when the native library is loaded and its ABI is compatible.</summary>
    public static bool IsNativeAvailable => _isAvailable.Value;

    /// <summary>ABI version reported by the loaded library, or 0 when unavailable.</summary>
    public static uint AbiVersion { get; private set; }

    /// <summary>Full path of the loaded library, when it was resolved from a known location.</summary>
    public static string? LoadedPath { get; private set; }

    /// <summary>Why the native library is unavailable (empty when it loaded).</summary>
    public static string LoadError { get; private set; } = string.Empty;

    /// <summary>The runtime identifier used to locate the library, e.g. <c>win-x64</c>.</summary>
    public static string RuntimeIdentifier { get; } = ComputeRid();

    static NativeLoader()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver was already registered for this assembly.
        }
    }

    private static bool Initialize()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CRYPTONET_DISABLE_NATIVE"), "1", StringComparison.Ordinal))
        {
            LoadError = "Disabled by CRYPTONET_DISABLE_NATIVE=1.";
            return false;
        }

        try
        {
            AbiVersion = NativeMethods.cn_abi_version();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LoadError = $"cryptonet native library not found for {RuntimeIdentifier} ({ex.GetType().Name}). Using the managed fallback.";
            return false;
        }

        if ((AbiVersion >> 24) != (RequiredAbiVersion >> 24) || AbiVersion < RequiredAbiVersion)
        {
            LoadError = $"cryptonet ABI 0x{AbiVersion:X8} is incompatible (requires 0x{RequiredAbiVersion:X8}, same major). Using the managed fallback.";
            return false;
        }
        return true;
    }

    private static string ComputeRid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? (IsMusl() ? "linux-musl" : "linux")
            : "unknown";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    private static bool IsMusl()
    {
        try
        {
            return File.Exists("/lib/ld-musl-x86_64.so.1") || File.Exists("/lib/ld-musl-aarch64.so.1");
        }
        catch
        {
            return false;
        }
    }

    private static string FileName =>
        OperatingSystem.IsWindows() ? "cryptonet.dll" : OperatingSystem.IsMacOS() ? "libcryptonet.dylib" : "libcryptonet.so";

    /// <summary>Candidate paths searched before the default OS loader, in order.</summary>
    public static IEnumerable<string> CandidatePaths()
    {
        string file = FileName;
        string? overridePath = Environment.GetEnvironmentVariable("CRYPTONET_NATIVE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            yield return overridePath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        yield return Path.Combine(dir.FullName, file);
        yield return Path.Combine(dir.FullName, "runtimes", RuntimeIdentifier, "native", file);

        // Development layout: walk up to the repository root (runtimes/ or rust/target/release/).
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "runtimes", RuntimeIdentifier, "native", file);
            yield return Path.Combine(dir.FullName, "rust", "target", "release", file);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName) return IntPtr.Zero;

        foreach (string candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                LoadedPath = candidate;
                return handle;
            }
        }

        return NativeLibrary.TryLoad(LibraryName, assembly, searchPath, out IntPtr fallback) ? fallback : IntPtr.Zero;
    }
}
