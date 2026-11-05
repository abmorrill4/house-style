---
title: Lint a project
description: Check every topic and snippet, or only the folders you pick, and work one rule at a time.
sidebar:
  order: 2
---

Open any topic in the project, and then select **House Style > Lint Project**. House Style lints every topic and snippet against the style rules. It uses no AI and costs nothing.

The top of the pane sums up the result, for example: *117 issues in 42 files. 63 can be changed automatically across 9 rules.*

## Lint only some folders

To lint part of the project, select the arrow on **Lint Project**, and then select **Lint Folders…**. Pick one or more folders (hold Ctrl or Shift to pick several), and then select **Lint**. A folder includes its subfolders. The pane names the folders it covers, and works the same as for a whole project. Files you left out of Lint Project stay out, and a folder that holds only excluded files isn't listed.

If the project has no folders with topics or snippets in them, House Style tells you to use **Lint Project** instead. Lint Folders uses no AI and costs nothing, the same as Lint Project. The title of the pane lists the folders you picked. For three or more folders it names the first two and says how many more.

## Work one rule at a time

The pane lists one row per rule, the most frequent first. Each row says how many issues the rule has, in how many files, and their type: **Auto-fix** when every one has an exact fix, **Needs you** when none does, or how many are Auto-fix. Select a rule to see why it matters, examples, and the files it's in. Then choose:

- **Change All…**: makes the rule's Auto-fix changes in every file. You see the files and counts first. Nothing changes until you confirm.
- **Review One by One**: lists only that rule's issues. Select **All Rules**, or press Esc, to go back.
- **Disable Rule…**: turns the rule off or changes its level. If you can't change your team's guide, this is **Request Rule Change…**.
- **Skip**: hides the rule until the next lint. **Show Skipped Rules** brings skipped rules back.

To list issues file by file instead, select **By file** (Alt+F). **By rule** (Alt+R) goes back. The pane remembers your choice.

## Go to an issue

- **Go To** opens the file and selects the text.
- **Open File** opens the topic or snippet without selecting anything.
- Right-click an issue for **Go To**, **Open File**, **Copy File Path** and **Report False Positive…**.

## Leave files out

To stop linting a file in Lint Project, right-click one of its issues and select **Exclude File from Lint Project**. To leave out its whole folder, select the **Exclude Folder … from Lint Project** item, which names the folder. The exclusion applies to everyone who works on the project, and to the lint before a build. **Lint Topic** still lints an excluded topic.

To lint an excluded file again, select **Excluded Files (n)** at the top of the pane, select the file or folder, and then select **Include Again**.

## Export the issues

To plan the work in Excel, select **Export Issues** at the top of the pane and save the file. The file is a CSV that opens in Excel. Each row has the topic, level, issue, rule, source, the text, the suggested text, a status and a link that opens the topic file. Use the **Status** column to filter out issues you already changed.

## Reopen the pane

If you close the pane, select **House Style > Reopen Issues**. It brings back your last results without linting again.

## Fix everything that can be fixed

**Fix All (n)…** makes every Auto-fix change in the project, except rules you skipped. You see a preview first. A long run shows its progress. To stop it, select **Stop after this file**. Files already changed keep their changes.

Each file's changes are tracked changes, so you can accept or reject each one in Flare's **Review** tab. Files that weren't open are saved and closed. Files you have open stay open, unsaved. Read-only files, and text that changed since the lint, are skipped and listed.

If you undo a change in a file, its issue comes back in the pane.

When you fix issues by hand in a file, they show as fixed when you stop typing or save the file. When you choose Change on a word inside a longer issue, the longer issue stays listed until you lint again.

For an AI review, open a topic and select **Lint Topic**.
