---
title: Troubleshoot
description: Fixes for a missing ribbon tab, AI that does not work, a team guide that cannot be read, and more.
sidebar:
  order: 1
---

## Report a problem

Select **Help > Report a Problem…**, describe the problem or the change you'd like, and select **Send**. **Include diagnostic details** adds your House Style and Flare versions, your Windows version, your settings without keys or folder paths, and the end of `%APPDATA%\FlareAI\diag.log`. The log has timings and errors, never your topic text or your key. A report never includes your topic text unless you type it in.

When House Style hits an error, **House Style ran into a problem** opens. Add what you were doing if you like, and select **Send** or **Don't Send**. **Show Details** shows exactly what is sent. To send error reports without being asked, check **Send error reports automatically**, here or in **Settings > Advanced**.

If House Style can't send the report, it saves it as a zip file in **Documents\House Style Reports** and tells you where. Attach the file to a new issue on GitHub or to an email. Select **Open GitHub Issues** or **Email Report**, depending on how your IT department set it up, or **Open Folder** to find the file. **Send** becomes **Try Again**.

## An issue is wrong

Right-click the issue in the pane and select **Report False Positive…**, then pick a reason. The rule, the flagged words and your reason are sent. The surrounding sentence is sent only if you check **Include the surrounding sentence**. The issue stays in the list, so you can still Ignore it.

**Diagnostics…** in **House Style > Settings > Advanced** shows your version, whether updates are on, what House Style takes as the selection, and the AI use of this session.

## House Style isn't on the ribbon

- Select **File > Options > Plugins**, and check that House Style is enabled. If the tab still doesn't appear, restart Flare.
- Switch to the Ribbon interface: **View > Interface**.
- Run **Install.cmd** again from the download, with Flare closed.

## The AI doesn't work

- Select **House Style > Settings**, and on the **AI** tab select **Test connection**. The result says what to check.
- For a gateway, paste the whole **Gateway key**, with no spaces. If the test says the model isn't allowed, ask your gateway admin to allow the Claude models for your key.
- If the pane says "AI review is off", **No AI** is chosen on the **AI** tab.

## "Your team's style guide couldn't be read"

House Style can't reach the team's guide folder, for example because you're offline. It lints with the copy it saved the last time it could reach the folder, and says the date of that copy after each lint. If it has no copy yet, it lints with Google's guide, the starter rules and your own changes. You can't change the team's rules until the folder is back. Check the folder in **Settings > Style guide**.

## "Your team's style guide has no rules yet"

The folder you chose in **Settings > Style guide** is a Flare project with nothing House Style can read in `Content/Resources/StyleGuide`. House Style lints with Google's guide until you add rules. See [Maintain the style guide](/docs/team-style-guide/maintain-the-style-guide/).

## An update didn't install

You stay on your version, and it works as before. The notice says why. House Style tries again later. To install it yourself, download it and run **Install.cmd**.

## House Style refused a change

House Style refuses any AI result that would drop or change a variable, cross-reference, snippet or condition. Nothing in the topic changes. Report it.

## Known Flare limitation

House Style won't restructure a paragraph that contains a variable, for example split it or make it a list, because Flare can't redo (Ctrl+Y) that kind of change. The result window says why, and nothing changes. Wording-only edits and word-level fixes work. Make the structural change yourself.

House Style also won't turn one kind of block into another, for example a list into a table or a paragraph into a list, because Flare can't make that change as a tracked change. Make that change yourself, or ask for an edit that keeps the first block, for example an intro paragraph followed by a list.
