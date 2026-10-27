# House Style tester guide

Thank you for testing House Style. This page gets you started. The full user guide is a Flare project in
`docs/user-guide` (open `FlareAI-Guide.flprj`). For what changed since 0.2, see **House Style > Help > What's New** or
`docs/release-notes/0.3.0.md`.

## Install or upgrade

1. Extract the whole download.
2. Close Flare.
3. Double-click `Install.cmd` and approve the administrator prompt. It replaces a copy you installed by hand and keeps
   your settings.
4. Start Flare. The installer turns House Style on for you. If the tab is missing, select
   **File > Options > Plugins > House Style > Enable**.

If you had an earlier test build that updated itself, run `Install.cmd` from this download once too.

From now on, House Style updates itself. When a new version is out, it downloads and checks it in the background, and
a notice says it installs when you close Flare. Your next start shows what's new.

If House Style isn't on the ribbon, see the [install page](https://gethousestyle.com/install.html).

## Set up

New writers see a four-step setup when Flare starts. You already have settings, so open it yourself:
**House Style > Settings > Run setup again**.

1. **How will you use House Style?** Choose **To check my own writing**, or **To set up the style guide for my team**
   if you own the style guide. Your answer decides the ribbon after you restart Flare.
2. **How will you use AI?** For your company's gateway, choose **My company's AI service (gateway)**. Enter the
   **Base URL** and **Gateway key**, and select **Test connection**.
3. **Whose rules?** Choose **My team's shared style guide folder** and browse to the folder your style guide owner
   gave you. Or keep **Google's guide plus my own changes**.
4. Select **Finish**. Then try the short practice, or select **Help > Start the Tour Again** later.

## Try these

| Do this | What to look for |
|---|---|
| Open a topic and select **Lint Topic**. | Issues from the style rules at once, AI issues shortly after. Each issue is **Auto-fix** or **Needs you**. |
| Select an issue and select **Change**. Then press Ctrl+Z. | The change is a tracked change. After Ctrl+Z, the issue is back in the pane. |
| Select **Fix All** in the pane. | Every Auto-fix change in the topic is made. One Ctrl+Z undoes them. |
| Select **Lint Project**. | Issues grouped by rule. Try **Change All…**, **Review One by One**, **Go To** and **Open File**. Right-click an issue. |
| Select **Rules**. | Every rule, with search and filters. Disable one, then enable it again. |
| Select **Disable Rule…** on an issue. | If you're an owner, the rule is disabled on your next lint. If not, you see **Request Rule Change…**. |
| Right-click an issue and select **Report False Positive…**. | Pick a reason and send. The issue stays in the list. |
| In Settings, choose **No AI**, and lint a topic again. | The pane says the AI review is off and shows no AI issues. |
| As an owner, select **Import Rules > Add notes or an old guide…**, and load a Word file. | Suggested rules you can edit or remove before you add them. |

## Changing the team's style guide

Your team's guide has **owners**, listed in the guide's `FlareAI/owners.txt`. Only owners change the team's rules.
The guide folder is read-only for everyone else. Anyone listed in `owners.txt` gets the **Style Guide** group on the
ribbon.

**If you're a writer:**

- Select **Request Rule Change…** on an issue, or a **Request…** button in **Rules**. Say what you want and why.
- The rule stays as it is until an owner decides. Its issues show **Request sent**.
- On your next lint after the owner decides, a notice in the pane says **approved** or **declined**, with the owner's
  note. An approved exception for your project is written into the project for you.
- To see or withdraw your requests, select **My Requests**. To ask for new rules from a document, select
  **Import Rules…** there.

**If you're an owner:**

- Change rules directly from the pane or **Rules**. Writers get the change on their next lint.
- Select **Rule Decisions** to see requests under **Requests to the style guide owners**. Select **Approve or
  decline…**. You can adjust a change before you approve it.
- Requests arrive through the team feedback folder. Every writer sets it in setup or in **Settings > Style guide**. Without it, writers
  can't send requests.

**Limits:**

- Changes made to the guide outside House Style aren't recorded.
- If a writer can't reach the guide folder, for example offline, House Style lints with Google's guide only and says so.
- Requests contain the rule, the words it flagged, the topic's filename and the reason. They contain a writer's name
  only if the guide's `attribution.txt` says `names`.

Setting up a guide for a team, step by step: user guide topic *Set up a team style guide*. Publishing it to
Confluence: *Publish the style guide to Confluence*.

## Known issues

- A change to **How will you use House Style?** in Settings changes the ribbon the next time you start Flare.
- House Style won't restructure a paragraph that contains a variable (split it, make it a list), because Flare crashes
  if that change is redone (Ctrl+Y). You see why, and nothing changes. Wording-only edits work. To fix a "use the
  variable" issue, select **Go To**, then use **Insert > Variable**.
- House Style won't turn one kind of block into another, for example a list into a table or a paragraph into a list,
  because Flare can't make that change as a tracked change. You see why, and nothing changes. A change that keeps the
  first block's kind works, for example a paragraph split into an intro paragraph and a list.
- **Rewrite…** can't restructure a paragraph with pending tracked changes. Accept or reject them first.
- House Style refuses any AI change that would drop or change a variable, cross-reference, snippet or condition. You
  see why, and nothing changes. Report it anyway.

## Report a problem

- Select **Help > Report a Problem…**. Say what happened, select **Preview** to see exactly what is sent, and select
  **Send**.
- When House Style hits an error, **House Style ran into a problem** opens. Select **Send**, or check
  **Send error reports automatically** to send later ones without being asked.
- Reports never include your topic text unless you type it in, and never your key or folder paths.
- If a report can't be sent, it's saved in **Documents\House Style Reports**. Attach it to a new issue on GitHub.
