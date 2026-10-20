using System.Runtime.InteropServices;
using Avalonia.Threading;
using PromptForge.Core.Settings;
using PromptForge.Mac.Native;

namespace PromptForge.Mac.Hotkeys;

internal enum HotkeyAction
{
    Optimize = 1,
    OptimizeSelection = 2,
    Voice = 3,
}

/// <summary>
/// System-wide hotkeys via Carbon's RegisterEventHotKey. macOS delivers only the registered chords to
/// PromptForge, so no Input Monitoring permission is needed and no other keystrokes are ever seen.
/// Press and release are both reported (release drives hold-to-talk dictation).
/// </summary>
internal sealed unsafe class HotkeyManager : IDisposable
{
    private const uint Signature = 0x50466732; // 'PFg2'
    private const uint ProbeId = 99;

    private static HotkeyManager? _instance;
    private readonly Dictionary<HotkeyAction, (HotkeyChord Chord, IntPtr Ref)> _registered = [];
    private IntPtr _handlerRef;

    public HotkeyManager()
    {
        _instance = this;
        var specs = stackalloc Carbon.EventTypeSpec[2];
        specs[0] = new Carbon.EventTypeSpec { EventClass = Carbon.EventClassKeyboard, EventKind = Carbon.EventHotKeyPressed };
        specs[1] = new Carbon.EventTypeSpec { EventClass = Carbon.EventClassKeyboard, EventKind = Carbon.EventHotKeyReleased };
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> handler = &OnEvent;
        var status = Carbon.InstallEventHandler(Carbon.GetApplicationEventTarget(), (IntPtr)handler, 2, specs, IntPtr.Zero, out _handlerRef);
        if (status != 0) Log.Error($"hotkey handler install failed (OSStatus {status})");
    }

    public event Action<HotkeyAction>? Pressed;
    public event Action<HotkeyAction>? Released;

    public IReadOnlyDictionary<HotkeyAction, HotkeyChord> Registered => _registered.ToDictionary(r => r.Key, r => r.Value.Chord);

    /// <summary>Registers a hotkey. Returns an error message, or null on success.</summary>
    public string? Register(HotkeyAction action, string text)
    {
        Unregister(action);
        if (!HotkeyChord.TryParse(text, out var chord)) return $"\"{text}\" isn't a valid shortcut.";
        if (MacKeys.Problem(chord) is { } problem) return problem;
        MacKeys.TryGetKeyCode(chord, out var code);

        var status = Carbon.RegisterEventHotKey(code, Modifiers(chord), new Carbon.EventHotKeyId { Signature = Signature, Id = (uint)action },
            Carbon.GetApplicationEventTarget(), Carbon.HotKeyExclusive, out var hotKeyRef);
        if (status != 0)
        {
            return status == Carbon.HotKeyExistsError
                ? $"{MacKeys.Display(chord)} is already used by another app. Pick a different shortcut in Settings."
                : $"Couldn't register {MacKeys.Display(chord)} (OSStatus {status}).";
        }
        _registered[action] = (chord, hotKeyRef);
        return null;
    }

    public void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action, out var entry)) Carbon.UnregisterEventHotKey(entry.Ref);
    }

    public void UnregisterAll()
    {
        foreach (var action in _registered.Keys.ToList()) Unregister(action);
    }

    /// <summary>Checks whether a shortcut is free by registering it briefly under a scratch id.</summary>
    public string? Probe(string text)
    {
        if (!HotkeyChord.TryParse(text, out var chord)) return $"\"{text}\" isn't a valid shortcut.";
        if (MacKeys.Problem(chord) is { } problem) return problem;
        if (_registered.Values.Any(r => r.Chord == chord)) return null; // already ours
        MacKeys.TryGetKeyCode(chord, out var code);
        var status = Carbon.RegisterEventHotKey(code, Modifiers(chord), new Carbon.EventHotKeyId { Signature = Signature, Id = ProbeId },
            Carbon.GetApplicationEventTarget(), Carbon.HotKeyExclusive, out var hotKeyRef);
        if (status != 0) return $"{MacKeys.Display(chord)} is already used by another app.";
        Carbon.UnregisterEventHotKey(hotKeyRef);
        return null;
    }

    private static uint Modifiers(HotkeyChord chord) =>
        (chord.Ctrl ? Carbon.ControlKey : 0) | (chord.Alt ? Carbon.OptionKey : 0) | (chord.Shift ? Carbon.ShiftKey : 0) | (chord.Win ? Carbon.CmdKey : 0);

    [UnmanagedCallersOnly]
    private static int OnEvent(IntPtr handlerCall, IntPtr eventRef, IntPtr userData)
    {
        try
        {
            Carbon.EventHotKeyId id;
            var status = Carbon.GetEventParameter(eventRef, Carbon.ParamDirectObject, Carbon.TypeEventHotKeyId, IntPtr.Zero,
                (nuint)sizeof(Carbon.EventHotKeyId), IntPtr.Zero, &id);
            if (status != 0 || id.Signature != Signature || _instance is not { } manager) return 0;
            var action = (HotkeyAction)id.Id;
            var released = Carbon.GetEventKind(eventRef) == Carbon.EventHotKeyReleased;
            // Delivered on the main thread inside the Cocoa run loop; hop onto Avalonia's dispatcher for the handlers.
            Dispatcher.UIThread.Post(() => (released ? manager.Released : manager.Pressed)?.Invoke(action));
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind into native code.
            Log.Error("hotkey callback failed", ex);
        }
        return 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_handlerRef != IntPtr.Zero) Carbon.RemoveEventHandler(_handlerRef);
        _handlerRef = IntPtr.Zero;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}
