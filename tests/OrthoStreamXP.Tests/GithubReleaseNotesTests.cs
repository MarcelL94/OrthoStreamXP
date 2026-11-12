using System.Text.Json;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tests;

/// <summary>Release notes read from a GitHub Release, for updates that carry none.</summary>
public sealed class GithubReleaseNotesTests
{
    private static string Release(string? body) => JsonSerializer.Serialize(new { tag_name = "v1.2.2", body });

    [Fact]
    public void The_written_notes_are_kept_and_the_generated_list_left_out()
    {
        string json = Release("## What's new\r\n\r\n- **Clearer help.** Plain words (#21).\r\n\r\n## What's Changed\r\n* Improved CI by @someone in https://github.com/x/y/pull/19\r\n\r\n**Full Changelog**: https://github.com/x/y/compare/v1.2.1...v1.2.2");

        Assert.Equal("## What's new\n\n- **Clearer help.** Plain words (#21).", GithubReleaseNotes.Parse(json));
    }

    [Fact]
    public void Notes_without_a_generated_list_are_kept_whole()
    {
        Assert.Equal("## Fixes\n\n- One.", GithubReleaseNotes.Parse(Release("## Fixes\n\n- One.\n")));
    }

    [Theory]
    [InlineData("## What's Changed\n* A pull request by @someone")]
    [InlineData("")]
    [InlineData(null)]
    public void A_release_with_only_the_generated_list_has_no_notes(string? body) => Assert.Null(GithubReleaseNotes.Parse(Release(body)));
}
