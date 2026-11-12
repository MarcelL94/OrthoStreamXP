using System.Text;

namespace OrthoStreamXP.Gui;

/// <summary>What a line of the release notes is.</summary>
internal enum NoteKind
{
    Heading,
    Bullet,
    Paragraph,
}

/// <summary>A piece of a line's text, bold or not.</summary>
internal sealed record NoteSpan(string Text, bool Bold);

/// <summary>One heading, bullet or paragraph of the release notes.</summary>
internal sealed record NoteBlock(NoteKind Kind, IReadOnlyList<NoteSpan> Spans)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// Turns the release notes' Markdown (release-notes/vX.Y.Z.md: headings, bullets, paragraphs, bold) into blocks the update
/// window shows. Links keep their text, inline code its content; anything else is shown as written.
/// </summary>
internal static class ReleaseNotes
{
    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var paragraph = new StringBuilder();
        void EndParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new NoteBlock(NoteKind.Paragraph, Inline(paragraph.ToString())));
            paragraph.Clear();
        }

        foreach (string raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || IsRule(line))
            {
                EndParagraph();
            }
            else if (line.StartsWith('#'))
            {
                EndParagraph();
                blocks.Add(new NoteBlock(NoteKind.Heading, Inline(line.TrimStart('#').Trim())));
            }
            else if (line.Length > 1 && line[0] is '-' or '*' or '+' && line[1] == ' ')
            {
                EndParagraph();
                blocks.Add(new NoteBlock(NoteKind.Bullet, Inline(line[2..].Trim())));
            }
            else if (blocks.Count > 0 && blocks[^1].Kind == NoteKind.Bullet && paragraph.Length == 0 && char.IsWhiteSpace(raw[0]))
            {
                // An indented line goes on with the bullet above it.
                var bullet = blocks[^1];
                blocks[^1] = bullet with { Spans = [.. bullet.Spans, .. Inline(" " + line)] };
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line);
            }
        }
        EndParagraph();
        return blocks;
    }

    /// <summary>A horizontal rule: ---, *** or ___.</summary>
    private static bool IsRule(string line) => line.Length >= 3 && line[0] is '-' or '*' or '_' && line.All(c => c == line[0]);

    /// <summary>Splits a line at its **bold** markers, and drops the link and code markup.</summary>
    private static List<NoteSpan> Inline(string text)
    {
        var spans = new List<NoteSpan>();
        string[] parts = Plain(text).Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            // An unclosed ** stays as written.
            bool bold = i % 2 == 1 && i < parts.Length - 1;
            string part = i % 2 == 1 && !bold ? "**" + parts[i] : parts[i];
            if (part.Length > 0) spans.Add(new NoteSpan(part, bold));
        }
        return spans;
    }

    /// <summary>[text](url) becomes text, `code` becomes code.</summary>
    private static string Plain(string text)
    {
        var plain = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '[' && text.IndexOf(']', i) is var close and > 0 && close + 1 < text.Length && text[close + 1] == '('
                && text.IndexOf(')', close) is var end and > 0)
            {
                plain.Append(text, i + 1, close - i - 1);
                i = end;
            }
            else if (text[i] != '`')
            {
                plain.Append(text[i]);
            }
        }
        return plain.ToString();
    }
}
