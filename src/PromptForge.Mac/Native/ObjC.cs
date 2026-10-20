using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PromptForge.Mac.Native;

/// <summary>
/// Minimal Objective-C runtime bridge for the few AppKit calls PromptForge needs (pasteboard,
/// running applications, alerts, login item). Every message signature used here passes only
/// pointers and integers, so one objc_msgSend binding per arity is ABI-correct on arm64 and x64.
/// </summary>
internal static partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";
    private static readonly ConcurrentDictionary<string, IntPtr> Selectors = new();
    private static readonly ConcurrentDictionary<string, IntPtr> Classes = new();

    static ObjC()
    {
        // Load the frameworks whose classes are looked up by name below.
        foreach (var framework in new[] { "AppKit", "ServiceManagement" })
            NativeLibrary.TryLoad($"/System/Library/Frameworks/{framework}.framework/{framework}", out _);
    }

    [LibraryImport(Lib, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClass(string name);

    [LibraryImport(Lib, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RegisterSelector(string name);

    [LibraryImport(Lib)]
    internal static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(Lib)]
    internal static partial void objc_autoreleasePoolPop(IntPtr pool);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Msg0(IntPtr receiver, IntPtr selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Msg1(IntPtr receiver, IntPtr selector, IntPtr a);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Msg2(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);

    public static IntPtr Class(string name) => Classes.GetOrAdd(name, GetClass);

    private static IntPtr Sel(string name) => Selectors.GetOrAdd(name, RegisterSelector);

    public static IntPtr Send(IntPtr receiver, string selector) => receiver == IntPtr.Zero ? IntPtr.Zero : Msg0(receiver, Sel(selector));

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr a) => receiver == IntPtr.Zero ? IntPtr.Zero : Msg1(receiver, Sel(selector), a);

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr a, IntPtr b) => receiver == IntPtr.Zero ? IntPtr.Zero : Msg2(receiver, Sel(selector), a, b);

    /// <summary>BOOL results are one byte; the rest of the return register is undefined on x64.</summary>
    public static bool SendBool(IntPtr receiver, string selector) => (Send(receiver, selector) & 0xFF) != 0;

    public static bool SendBool(IntPtr receiver, string selector, IntPtr a) => (Send(receiver, selector, a) & 0xFF) != 0;

    public static bool SendBool(IntPtr receiver, string selector, IntPtr a, IntPtr b) => (Send(receiver, selector, a, b) & 0xFF) != 0;

    /// <summary>
    /// An autoreleased NSString (a CFString is the same object), built from UTF-16 so every character,
    /// including NUL, survives. Call inside an <see cref="AutoreleasePool"/>.
    /// </summary>
    public static IntPtr NSString(string value) => Send(CF.CreateString(value), "autorelease");

    public static string? ToManagedString(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    /// <summary>An autoreleased NSData copy of the bytes.</summary>
    public static unsafe IntPtr NSData(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes)
            return Send(Class("NSData"), "dataWithBytes:length:", (IntPtr)p, bytes.Length);
    }

    public static byte[] ToBytes(IntPtr nsData)
    {
        if (nsData == IntPtr.Zero) return [];
        var length = (long)Send(nsData, "length");
        if (length <= 0) return [];
        var result = new byte[length];
        Marshal.Copy(Send(nsData, "bytes"), result, 0, result.Length);
        return result;
    }

    public static IEnumerable<IntPtr> Items(IntPtr nsArray)
    {
        var count = (long)Send(nsArray, "count");
        for (long i = 0; i < count; i++) yield return Send(nsArray, "objectAtIndex:", (IntPtr)i);
    }

    public static IntPtr SharedApplication => Send(Class("NSApplication"), "sharedApplication");

    /// <summary>Brings PromptForge's own windows forward (it has no Dock icon, so macOS won't do it on its own).</summary>
    public static void ActivateSelf()
    {
        using var pool = new AutoreleasePool();
        Send(SharedApplication, "activateIgnoringOtherApps:", 1);
    }
}

/// <summary>Drains autoreleased objects created by synchronous native calls. Never hold one across an await.</summary>
internal readonly struct AutoreleasePool : IDisposable
{
    private readonly IntPtr _pool;

    public AutoreleasePool() => _pool = ObjC.objc_autoreleasePoolPush();

    public void Dispose() => ObjC.objc_autoreleasePoolPop(_pool);
}
