# Release notes

One Markdown file per release, named after its tag: `v1.2.0.md` for the tag `v1.2.0`.

When the tag is pushed, the [release workflow](../.github/workflows/release.yml) puts the file's text at the top of
the GitHub Release. GitHub's generated list of changes (merged pull requests, new contributors) follows it.
`build-installer.ps1` also packs the file into the update feed: the app shows it in its update window before
installing (headings, bullets, paragraphs and **bold**; links show their text only). For an update packed without
notes, the app reads them from the GitHub Release instead, up to GitHub's generated "What's Changed" list.

## Releasing

1. Write `release-notes/vX.Y.Z.md` and commit it with the release.
2. Push the tag `vX.Y.Z`.

A release (`v1.2.0`) needs its notes file. If the file is missing or empty, the workflow stops at its first step,
before anything is built or published: add the file, then delete and push the tag again. A pre-release (`v0.x`, or a
suffix such as `v1.2.0-rc1`) may have no notes file; it then gets the generated list only.

## Writing the notes

The notes are for people who fly with OrthoStreamXP, not for developers: say what changed for them, in plain words.
GitHub already lists the pull requests below, so there is no need to repeat them.

```markdown
## What's new

- **Feature name.** What it does and where to find it.

## Fixes

- What no longer goes wrong.

## Good to know

- Anything to do after updating, or a change of behaviour people should expect.
```

Leave out the sections that have nothing in them. The page title ("OrthoStreamXP 1.2.0") is set by the workflow,
so the file needs no title of its own.
