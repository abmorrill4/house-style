---
title: Set up a team style guide
description: Create the shared guide, name the owners, set up the feedback folder, and decide requests.
sidebar:
  order: 1
---

This topic is for style guide owners. A team shares one style guide, a Flare project in a shared folder. Owners change it; other writers send requests.

## Create the guide

1. Create a folder that every writer can reach: OneDrive, SharePoint, Google Drive or a network drive.
2. Copy the starter project into it. Download it from the [House Style page on GitHub](https://github.com/abmorrill4/house-style/tree/main/styleguide): it's the `styleguide` folder. Copy `StyleGuide.flprj`, `Project`, `FlareAI` and `Content/Resources/StyleGuide/Company`. Don't copy the `Google` folder: House Style always loads Google's rules, and its own default changes to them, from the plugin. That way, updates reach your team.
3. Set the folder's permissions: owners can edit, everyone else can only read.
4. In the `FlareAI` folder, copy `owners.example.txt` to `owners.txt`, and list two or more owners, one per line. Use each owner's email address or Windows user name. Anyone listed gets the Style Guide group on the ribbon, whatever they chose in setup.
5. Create a second shared folder for team feedback. Every writer can edit it. Requests and review decisions go there.
6. Tell the writers both folder paths. Each writer chooses the guide folder in setup or in **Settings > Style guide**, and the feedback folder in setup or in **Settings > Style guide > Team feedback folder**.

To change one of House Style's default changes to Google's rules, add a row for the same rule to your guide's Rule/Setting table. Your row wins.

## Decide requests

Select **House Style > Rule Decisions**. Open requests are listed under **Requests to the style guide owners**, with the writer's reason and example text.

- Select **Approve or decline…**. You can adjust the change before you approve it, and add a note.
- An approved change is written to the guide. Writers get it on their next lint.
- An approved project exception is written into that writer's project by their copy of House Style.
- The writer sees your decision and note on their next lint, and in **My Requests**.

Owners change rules directly, from the issues pane or **Rules**. To see every rule that is disabled or changed, with who, when and why, select **Disabled Rules…** in Rule Decisions.

## Files in the guide's FlareAI folder

| File | What it does |
|---|---|
| `owners.txt` | The owners. If the file exists but a writer can't read it, every change from that writer goes to the owners as a request. |
| `layer.txt` | A fixed name for the guide's rules, so rule references match on every computer. |
| `attribution.txt` (copy `attribution.example.txt`) | `attribution = counts` (the default) or `attribution = names`. See below. |
| `models.json` | The AI models every writer uses. See [Models and cost](/docs/settings-and-privacy/models-and-cost/). |
| `prompts/*.md` | Your team's versions of the AI's instructions. Create them with **Customize AI prompts…** in Rule Decisions. |

Changes to `owners.txt` and to folder permissions apply at the next rule change, without restarting Flare.

## What the team feedback folder holds

Each writer's decisions on issues (change, ignore, edit) and requests: the rule, the words it flagged, the reason if one was given, and the topic's filename. Who made each one depends on `attribution.txt`:

- `counts`: no names. Each computer has a random ID, so owners see "3 writers", not who.
- `names`: each writer's email address is recorded, and Rule Decisions shows it.

Without a team feedback folder, decisions stay on each writer's computer, and writers can't send requests.

## Keep the guide in source control instead

If your team uses Git, keep the guide in a repository. Each writer clones it and chooses the clone in Settings. When a clone is behind, the pane says so: pull to get the latest rules. Protect the main branch so only owners can merge.

## Limits

- Changes made to the guide outside House Style aren't recorded.
- If a writer can't reach the guide folder, for example offline, House Style lints with the copy it saved the last time it could, and says the date of that copy. With no copy yet, or if the writer no longer has permission to read the folder, it lints with Google's guide, the starter rules and the writer's own changes, and says why.
