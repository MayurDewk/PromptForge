namespace PromptForge.Core.Voice;

/// <summary>
/// Cleans dictated text before it is pasted. Control characters are removed (an escape sequence could
/// act on a terminal), and for terminals line breaks become spaces, so a paste can never run a command.
/// </summary>
public static class TranscriptSanitizer
{
    public static string Clean(string text, bool singleLine)
    {
        var chars = text.Replace("\r\n", "\n")
            .Select(c => singleLine && c is '\n' or '\r' or '\t' ? ' ' : c)
            .Where(c => !char.IsControl(c) || (!singleLine && c is '\n' or '\t'));
        return new string(chars.ToArray());
    }
}
