using System.Runtime.InteropServices;
using PromptForge.App.Native;

namespace PromptForge.App.TextAccess;

/// <summary>
/// Saves and restores the user's clipboard around our copy/paste round-trips, and writes our own
/// text with the flags that keep it out of Windows clipboard history and cloud sync.
/// Must be used on the UI (STA) thread.
/// </summary>
internal sealed class ClipboardSnapshot
{
    /// <summary>Formats password managers set on secrets they will clear on their own.</summary>
    private static readonly HashSet<string> SensitiveFormats = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];

    private readonly List<(string Format, object Data)> _items;

    private ClipboardSnapshot(List<(string, object)> items) => _items = items;

    public bool IsEmpty => _items.Count == 0;

    public static ClipboardSnapshot Capture()
    {
        var items = new List<(string, object)>();
        var data = Retry(() => Clipboard.GetDataObject());
        if (data is null) return new ClipboardSnapshot(items);

        foreach (var format in SafeFormats(data))
        {
            try
            {
#pragma warning disable WFDEV005 // GetData is used only to copy raw clipboard formats back unchanged.
                var value = data.GetData(format, autoConvert: false);
#pragma warning restore WFDEV005
                if (value is not null) items.Add((format, value));
            }
            catch (Exception ex) when (ex is ExternalException or COMException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                // Some formats (delay-rendered, private COM objects) can't be copied; skip them.
            }
        }
        return new ClipboardSnapshot(items);
    }

    public void Restore()
    {
        try
        {
            // A password manager clears its secret later only if the clipboard still holds what it wrote.
            // Restoring would leave the password behind as fresh clipboard data, so it is cleared instead.
            if (_items.Count == 0 || _items.Any(i => SensitiveFormats.Contains(i.Format)))
            {
                if (_items.Count > 0) Log.Info("clipboard held excluded-from-history data; cleared instead of restored");
                Retry(() => { Clipboard.Clear(); return true; });
                return;
            }
            var dataObject = new DataObject();
            foreach (var (format, value) in _items) dataObject.SetData(format, autoConvert: false, value);
            Retry(() => { Clipboard.SetDataObject(dataObject, copy: true, retryTimes: 5, retryDelay: 40); return true; });
        }
        catch (Exception ex) when (ex is ExternalException or COMException)
        {
            Log.Warn($"clipboard restore failed: {ex.GetType().Name}");
        }
    }

    private static IEnumerable<string> SafeFormats(IDataObject data)
    {
        string[] formats;
        try { formats = data.GetFormats(autoConvert: false); }
        catch (Exception ex) when (ex is ExternalException or COMException) { return []; }
        // Skip formats that are re-created automatically from others or can't round-trip.
        return formats.Where(f => f is not ("EnhancedMetafile" or "MetaFilePict" or "Object Descriptor" or "Link Source Descriptor"));
    }

    internal static T? Retry<T>(Func<T> action, int attempts = 6)
    {
        for (var i = 0; ; i++)
        {
            try { return action(); }
            catch (ExternalException) when (i < attempts - 1) { Thread.Sleep(30); }
        }
    }
}

internal static class ClipboardText
{
    public static uint Sequence => Win32.GetClipboardSequenceNumber();

    /// <summary>Reads Unicode text from the clipboard, or null when there is none.</summary>
    public static string? Read() =>
        ClipboardSnapshot.Retry(() => Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null);

    /// <summary>Puts text on the clipboard, marked to stay out of clipboard history and cloud clipboard.</summary>
    public static void WritePrivate(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", false, new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", false, new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", false, new MemoryStream(BitConverter.GetBytes(0)));
        ClipboardSnapshot.Retry(() => { Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 40); return true; });
    }

    /// <summary>Puts text on the clipboard normally, for when the user is meant to paste it themselves.</summary>
    public static void Write(string text) =>
        ClipboardSnapshot.Retry(() => { Clipboard.SetText(text, TextDataFormat.UnicodeText); return true; });

    public static async Task<bool> WaitForChangeAsync(uint before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Sequence != before) return true;
            await Task.Delay(15);
        }
        return Sequence != before;
    }
}

