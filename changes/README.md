# Notes for writers, one file per change

Every PR that changes something writers can see adds one file here, named after the branch or report
(`changes/edit-rule.md`, `changes/report-12.md`). It holds one or two bullets written from the writer's side, in the
copy style in `CLAUDE.md`:

```markdown
- Edit Rule and Delete Rule in Rules change or remove a rule in your team's style guide.
```

The first line of a note may be `Kind: New`, `Kind: Improved` or `Kind: Fixed`. The release notes and the What's new
window group the bullets under New, Improved and Fixed, in that order. A note with no kind counts as Improved.

```markdown
Kind: Fixed
- Lint Topic no longer stops when the topic is empty.
```

Start each bullet with a capital letter and end it with a full stop. Leave out colons, arrows, semicolons and
taglines. Two notes in one release that open with the same five words fail the notes test.

The nightly release collects the files added since the last release into that version's What's new section and its
release notes (`scripts/release/Build-WhatsNew.ps1`). Nobody edits a shared section, so PRs never conflict over notes.
A PR that changes `src/` or `styleguide/` without a note fails CI unless it has the `no-notes` label.
