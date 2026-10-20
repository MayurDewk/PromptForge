using PromptForge.Core.Voice;

namespace PromptForge.Core.Tests;

public class TranscriptSanitizerTests
{
    [Fact]
    public void Terminal_text_becomes_one_line_without_control_characters()
    {
        var cleaned = TranscriptSanitizer.Clean("ls -la\r\nrm -rf /tmp/x\n\u001b[31mred\u0007", singleLine: true);
        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\u001b', cleaned);
        Assert.DoesNotContain('\u0007', cleaned);
        Assert.Equal("ls -la rm -rf /tmp/x [31mred", cleaned);
    }

    [Fact]
    public void Text_fields_keep_line_breaks_and_tabs()
    {
        Assert.Equal("first\n\tsecond", TranscriptSanitizer.Clean("first\r\n\tsecond\u0000", singleLine: false));
    }
}
