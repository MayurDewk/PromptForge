using System.Runtime.InteropServices;

namespace PromptForge.Mac.Native;

internal static unsafe partial class CF
{
    private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly IntPtr Handle = NativeLibrary.Load(Lib);

    [StructLayout(LayoutKind.Sequential)]
    public struct CFRange
    {
        public nint Location;
        public nint Length;
    }

    [LibraryImport(Lib)]
    public static partial void CFRelease(IntPtr cf);

    [LibraryImport(Lib)]
    public static partial nuint CFGetTypeID(IntPtr cf);

    [LibraryImport(Lib)]
    public static partial nuint CFStringGetTypeID();

    [LibraryImport(Lib)]
    private static partial nint CFStringGetLength(IntPtr s);

    [LibraryImport(Lib)]
    private static partial void CFStringGetCharacters(IntPtr s, CFRange range, char* buffer);

    [LibraryImport(Lib)]
    private static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, char* chars, nint length);

    [LibraryImport(Lib)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

    [LibraryImport(Lib)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(Lib)]
    private static partial byte* CFDataGetBytePtr(IntPtr data);

    [LibraryImport(Lib)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    public static IntPtr BooleanTrue { get; } = ReadConstant(Handle, "kCFBooleanTrue");

    /// <summary>Reads an exported constant such as kSecClass (a global holding a CFTypeRef).</summary>
    public static IntPtr ReadConstant(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

    /// <summary>A new CFString the caller must CFRelease.</summary>
    public static IntPtr CreateString(string value)
    {
        fixed (char* p = value) return CFStringCreateWithCharacters(IntPtr.Zero, p, value.Length);
    }

    public static string? ToManagedString(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero || CFGetTypeID(cfString) != CFStringGetTypeID()) return null;
        var length = CFStringGetLength(cfString);
        if (length == 0) return "";
        var buffer = new char[length];
        fixed (char* p = buffer) CFStringGetCharacters(cfString, new CFRange { Location = 0, Length = length }, p);
        return new string(buffer);
    }

    /// <summary>A new CFData the caller must CFRelease.</summary>
    public static IntPtr CreateData(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes) return CFDataCreate(IntPtr.Zero, p, bytes.Length);
    }

    public static byte[] ToBytes(IntPtr cfData)
    {
        var length = CFDataGetLength(cfData);
        return length <= 0 ? [] : new ReadOnlySpan<byte>(CFDataGetBytePtr(cfData), (int)length).ToArray();
    }

    /// <summary>A new CFDictionary (retaining its keys and values) the caller must CFRelease.</summary>
    public static IntPtr CreateDictionary(IReadOnlyList<(IntPtr Key, IntPtr Value)> pairs)
    {
        var keys = pairs.Select(p => p.Key).ToArray();
        var values = pairs.Select(p => p.Value).ToArray();
        fixed (IntPtr* k = keys)
        fixed (IntPtr* v = values)
        {
            return CFDictionaryCreate(IntPtr.Zero, k, v, keys.Length,
                NativeLibrary.GetExport(Handle, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(Handle, "kCFTypeDictionaryValueCallBacks"));
        }
    }

    public static void Release(IntPtr cf)
    {
        if (cf != IntPtr.Zero) CFRelease(cf);
    }
}
