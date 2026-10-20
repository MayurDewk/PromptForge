using System.Runtime.InteropServices;

namespace PromptForge.Mac.Native;

/// <summary>CoreGraphics event posting and key state. Posting keystrokes needs the Accessibility permission.</summary>
internal static unsafe partial class CG
{
    private const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    public const int StatePrivate = -1;
    public const int StateHidSystem = 1;
    public const uint HidEventTap = 0;
    private const int FieldSourceUserData = 42;

    public const ulong FlagShift = 0x00020000;
    public const ulong FlagControl = 0x00040000;
    public const ulong FlagOption = 0x00080000;
    public const ulong FlagCommand = 0x00100000;
    public const ulong ModifierMask = FlagShift | FlagControl | FlagOption | FlagCommand;

    /// <summary>Marks our synthetic events so they can be told apart from the user's typing.</summary>
    public const long InputSignature = 0x50464721;

    [LibraryImport(Lib)]
    public static partial IntPtr CGEventSourceCreate(int stateId);

    [LibraryImport(Lib)]
    public static partial IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, byte keyDown);

    [LibraryImport(Lib)]
    public static partial void CGEventSetFlags(IntPtr @event, ulong flags);

    [LibraryImport(Lib)]
    public static partial void CGEventKeyboardSetUnicodeString(IntPtr @event, nuint length, char* text);

    [LibraryImport(Lib)]
    private static partial void CGEventSetIntegerValueField(IntPtr @event, int field, long value);

    [LibraryImport(Lib)]
    public static partial void CGEventPost(uint tap, IntPtr @event);

    [LibraryImport(Lib)]
    public static partial ulong CGEventSourceFlagsState(int stateId);

    [LibraryImport(Lib)]
    private static partial byte CGEventSourceKeyState(int stateId, ushort key);

    public static bool IsKeyDown(ushort key) => CGEventSourceKeyState(StateHidSystem, key) != 0;

    public static void MarkAsOurs(IntPtr @event) => CGEventSetIntegerValueField(@event, FieldSourceUserData, InputSignature);
}

/// <summary>Accessibility (AX) API: reading the focused control, and the permission check.</summary>
internal static partial class AX
{
    private const string Lib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private static readonly IntPtr Handle = NativeLibrary.Load(Lib);

    [LibraryImport(Lib)]
    private static partial byte AXIsProcessTrusted();

    [LibraryImport(Lib)]
    private static partial byte AXIsProcessTrustedWithOptions(IntPtr options);

    [LibraryImport(Lib)]
    public static partial IntPtr AXUIElementCreateSystemWide();

    [LibraryImport(Lib)]
    public static partial IntPtr AXUIElementCreateApplication(int pid);

    [LibraryImport(Lib)]
    private static partial int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

    [LibraryImport(Lib)]
    public static partial int AXUIElementSetMessagingTimeout(IntPtr element, float timeoutSeconds);

    public static bool IsTrusted => AXIsProcessTrusted() != 0;

    /// <summary>Asks macOS to show its "allow accessibility" prompt if PromptForge isn't trusted yet.</summary>
    public static bool PromptForTrust()
    {
        var key = CF.ReadConstant(Handle, "kAXTrustedCheckOptionPrompt");
        var options = CF.CreateDictionary([(key, CF.BooleanTrue)]);
        try
        {
            return AXIsProcessTrustedWithOptions(options) != 0;
        }
        finally
        {
            CF.Release(options);
        }
    }

    /// <summary>A copied attribute value (caller releases), or zero.</summary>
    public static IntPtr CopyAttribute(IntPtr element, string attribute)
    {
        if (element == IntPtr.Zero) return IntPtr.Zero;
        var name = CF.CreateString(attribute);
        try
        {
            return AXUIElementCopyAttributeValue(element, name, out var value) == 0 ? value : IntPtr.Zero;
        }
        finally
        {
            CF.Release(name);
        }
    }

    public static string? StringAttribute(IntPtr element, string attribute)
    {
        var value = CopyAttribute(element, attribute);
        try
        {
            return CF.ToManagedString(value);
        }
        finally
        {
            CF.Release(value);
        }
    }
}

/// <summary>Carbon hotkeys: the supported way to get system-wide shortcuts without watching all keystrokes.</summary>
internal static unsafe partial class Carbon
{
    private const string Lib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    public const uint CmdKey = 0x0100;
    public const uint ShiftKey = 0x0200;
    public const uint OptionKey = 0x0800;
    public const uint ControlKey = 0x1000;

    public const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    public const uint EventHotKeyPressed = 5;
    public const uint EventHotKeyReleased = 6;
    public const uint ParamDirectObject = 0x2D2D2D2D;  // '----'
    public const uint TypeEventHotKeyId = 0x686B6964;  // 'hkid'
    public const uint HotKeyExclusive = 1;
    public const int HotKeyExistsError = -9878;

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [LibraryImport(Lib)]
    public static partial IntPtr GetApplicationEventTarget();

    [LibraryImport(Lib)]
    public static partial int InstallEventHandler(IntPtr target, IntPtr handler, nuint numTypes, EventTypeSpec* list, IntPtr userData, out IntPtr handlerRef);

    [LibraryImport(Lib)]
    public static partial int RemoveEventHandler(IntPtr handlerRef);

    [LibraryImport(Lib)]
    public static partial int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, IntPtr target, uint options, out IntPtr hotKeyRef);

    [LibraryImport(Lib)]
    public static partial int UnregisterEventHotKey(IntPtr hotKeyRef);

    [LibraryImport(Lib)]
    public static partial int GetEventParameter(IntPtr @event, uint name, uint desiredType, IntPtr actualType, nuint bufferSize, IntPtr actualSize, void* data);

    [LibraryImport(Lib)]
    public static partial uint GetEventKind(IntPtr @event);

    [LibraryImport(Lib)]
    private static partial byte IsSecureEventInputEnabled();

    /// <summary>True while a password field (or an app such as a terminal's Secure Keyboard Entry) has secure input on.</summary>
    public static bool SecureInputActive => IsSecureEventInputEnabled() != 0;
}
