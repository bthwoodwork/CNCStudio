namespace CNCStudio.Core.Tests;

public class ErrorLogTests
{
    [Fact]
    public void Parses_entries_newest_first_with_kind_message_and_details()
    {
        Exception Thrown(Exception e) { try { throw e; } catch (Exception c) { return c; } }
        var text = ErrorLog.Format(new DateTime(2026, 11, 6, 22, 40, 0), Thrown(new TimeoutException("Inget svar från GRBL på \"$X\" inom 10 s.")))
                 + ErrorLog.Format(new DateTime(2026, 11, 6, 22, 50, 5), Thrown(new InvalidOperationException("Cannot set Visibility while a Window is closing.")));
        var entries = ErrorLog.Parse(text);
        Assert.Equal(2, entries.Count);
        Assert.Equal(new DateTime(2026, 11, 6, 22, 50, 5), entries[0].Time);
        Assert.Equal("InvalidOperationException", entries[0].Kind);
        Assert.Equal("Cannot set Visibility while a Window is closing.", entries[0].Message);
        Assert.Equal("TimeoutException", entries[1].Kind);
        Assert.Contains("Inget svar", entries[1].Message);
        Assert.Contains(" at ", entries[1].Details);                       // the stack trace is kept
    }

    [Fact]
    public void Empty_or_foreign_text_gives_no_entries()
    {
        Assert.Empty(ErrorLog.Parse(""));
        Assert.Empty(ErrorLog.Parse("hello\nworld"));
    }
}
