using OrthoStreamXP.Gui;

namespace OrthoStreamXP.Tests;

/// <summary>The release notes' Markdown, as the update window shows it.</summary>
public sealed class ReleaseNotesTests
{
    [Fact]
    public void Headings_bullets_and_paragraphs_become_blocks()
    {
        var blocks = ReleaseNotes.Parse("""
            ## What's new

            - **Clearer help.** The text explains
              the setting in plain words (#21).
            - Second change.

            First line of a paragraph
            and its second line.
            """);

        Assert.Equal([NoteKind.Heading, NoteKind.Bullet, NoteKind.Bullet, NoteKind.Paragraph], blocks.Select(b => b.Kind));
        Assert.Equal("What's new", blocks[0].Text);
        Assert.Equal("Clearer help. The text explains the setting in plain words (#21).", blocks[1].Text);
        Assert.Equal("Second change.", blocks[2].Text);
        Assert.Equal("First line of a paragraph and its second line.", blocks[3].Text);
    }

    [Fact]
    public void Bold_text_is_marked_and_its_markers_dropped()
    {
        var spans = ReleaseNotes.Parse("- **Bold.** Then plain, **bold again**.")[0].Spans;

        Assert.Equal([new NoteSpan("Bold.", true), new NoteSpan(" Then plain, ", false), new NoteSpan("bold again", true), new NoteSpan(".", false)], spans);
    }

    [Fact]
    public void An_unclosed_bold_marker_stays_as_written()
    {
        Assert.Equal("2 ** 3 is eight", ReleaseNotes.Parse("2 ** 3 is eight")[0].Text);
        Assert.DoesNotContain(ReleaseNotes.Parse("2 ** 3 is eight")[0].Spans, s => s.Bold);
    }

    [Fact]
    public void Links_keep_their_text_and_code_its_content()
    {
        var block = ReleaseNotes.Parse("See [the guide](https://example.com/guide) and set `cache` [here].")[0];

        Assert.Equal("See the guide and set cache [here].", block.Text);
    }

    [Fact]
    public void Rules_and_star_bullets_are_understood()
    {
        var blocks = ReleaseNotes.Parse("* One\n---\n* Two");

        Assert.Equal(["One", "Two"], blocks.Select(b => b.Text));
        Assert.All(blocks, b => Assert.Equal(NoteKind.Bullet, b.Kind));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\n ")]
    public void No_notes_give_no_blocks(string? markdown) => Assert.Empty(ReleaseNotes.Parse(markdown));
}
