# House Style test plan

For testers. Each case says what to do and what you should see. Mark it pass or fail. For a fail, use
**Help > Report a Problem…** in Flare, or file a report with [BUG-TEMPLATE.md](BUG-TEMPLATE.md). The user guide
(<https://gethousestyle.com/docs/>) explains every feature; this plan is how to check them.

## Setup (once)

1. Install House Style with `Install.cmd` (user guide: *Install and update*).
2. Select **House Style > Settings > Run setup again**, answer the four steps, and select **Test connection**.
3. The test corpus is in the repository: `corpus/DigitalBanking` (a Flare project: 25 feature areas × 10 topics,
   snippets, variables, conditions, images, a TOC and an HTML5 target) and `corpus/ground-truth.json`, which lists
   every problem planted in it: file, the exact words, and which check should catch it.
4. To start over, close the project in Flare and restore the folder from Git (`git checkout -- corpus`).

Planted problems (429 in all): wordy or banned words (`utilize`, `in order to`, `please`), legacy terms (`customers`,
`whitelist`, `Single Sign-On`), claims (`coming soon`, `guaranteed`), typed product names that should be variables,
Google rules (`e.g.`, `will`, `we`, `!`), judgment calls for the AI (passive voice, wordiness, vague steps,
unexplained `MFA`), and structure (images without alt text, skipped heading levels, a second h1, a table without a
header row, "click here", typed phone numbers and URLs). One shared snippet (`LegacyApp.flsnp`, used by 9 topics) says
"Customers".

## S. Setup, Settings and the ribbon

| # | Do | Expect |
|---|---|---|
| S1 | Rename `%APPDATA%\FlareAI\settings.json`, then start Flare. | The four-step setup opens by itself. Step 1 asks **How will you use House Style?** After **Finish**, the practice offer opens. Put the file back afterward. |
| S2 | In setup, choose the gateway and enter the Base URL as `http://<address>/` with a trailing slash. | It's saved as `https://<address>`, with no slash. |
| S3 | Paste half of the key and select **Test connection**. | A plain message about the key, not a generic error. |
| S4 | Open **Settings**. | Four tabs: AI, Style guide, Linting, Advanced. **Run setup again…** is at the bottom. |
| S5 | **File > Options > Plugins**. | House Style shows its real version. |
| S6 | Choose **To check my own writing**, restart Flare. | The tab has Lint Topic, Lint Project, Rules, Rewrite, Ask AI, Topic Metadata, Settings and Help. With a team guide set, **My Requests** too. |
| S7 | Choose **To set up the style guide for my team**, restart Flare. | The **Style Guide** group (Rule Decisions, Import Rules, Test Rules) is added. |
| S8 | Be listed in the team guide's `FlareAI/owners.txt` but choose **To check my own writing**, restart Flare. | The **Style Guide** group is still there. |

## T. Practice and getting started

| # | Do | Expect |
|---|---|---|
| T1 | **Help > Start the Tour Again**, **Practice on a sample project**. | The practice topic opens. A strip at the top of the pane shows **Step 1 of 5**. |
| T2 | Follow the five steps (Lint Topic, Change, Ctrl+Z, Disable Rule, the Review tab and **Next**). | Each step moves on by itself, except the Review tab step, which has **Next**. The last strip says you're ready. |
| T3 | Start the tour again and select **Skip tour**. | The strip goes away. |
| T4 | Open the pane with no issues during the first week. | A **Getting started** checklist. **Hide** puts it away for good. |

## A. Lint Topic

| # | Do | Expect |
|---|---|---|
| A1 | Open `transfers/about.htm`, put the caret anywhere, **Lint Topic**. | The issues pane opens; issues from the style rules at once, AI issues within about 20 seconds. Every planted problem in the topic is listed. |
| A2 | Select an issue. | The card shows its level, its source, its type (**Auto-fix** or **Needs you**), the words around it, the change, and the rule. |
| A3 | **Change** an Auto-fix issue. Then press Ctrl+Z. | A word-level tracked change, and the next issue is selected. After Ctrl+Z, the issue is back in the pane. |
| A4 | **Ignore ▾ > The suggestion is wrong** on an AI issue. | It disappears; `%APPDATA%\FlareAI\feedback.jsonl` gets a line for it. |
| A5 | Edit a sentence that has an issue, then select that issue. | It's marked as changed text and can't be changed from the pane; **Lint Paragraph** refreshes it. |
| A6 | **Rewrite…** on an issue, with "keep it shorter". | The issue updates. |
| A7 | On the "Customers who still use the legacy app" issue (from the snippet): **Change**. | The snippet opens, gets the tracked change, and the topic comes back to the front. The card said how many files use the snippet. |
| A8 | **Fix All (n)**. | Every Auto-fix change is made; one Ctrl+Z undoes them. |
| A9 | **Change All Issues (n)**, then Flare's **Review** tab. | Every issue with exact text, including AI rewording, is a tracked change you can accept or reject. |
| A10 | Close and reopen the topic, **Lint Topic**. | Saved issues appear at once, with no AI cost. Then **Lint Again**: the AI reviews each paragraph again (a fresh review, with AI cost). |
| A11 | Select a missing alt text issue. | The button reads **Add Alt Text…**; the draft is 125 characters or fewer; adding it is a tracked change. |
| A12 | In Settings, choose **No AI**, and lint the topic again. | The pane says the AI review is off and shows no AI issues, including saved ones. |
| A13 | Right-click an issue. | **Go To**, **Open File**, **Copy File Path** and **Report False Positive…**. |

## B. Rewrite and Ask AI

| # | Do | Expect |
|---|---|---|
| B1 | Select a paragraph with `In order to` and `prior to`, **Rewrite**. | Result window with the changes marked; **Change** gives word-level tracked changes; both phrases gone. |
| B2 | Select a paragraph that contains a variable and a bold UI label, **Rewrite**. | The variable and the bold label are untouched. |
| B3 | Select one step, **Ask AI** "split this into two steps". | The result offers **Restructure Instead…**; the restructured result is validated before you can make the change. |
| B4 | Select a step list, **Ask AI** "make this a table". | A clear refusal (Flare can't turn a list into a table as a tracked change; the window says why); never a broken topic. An answer that moves a condition to another item is refused. |
| B5 | Select a sentence with "should", **Ask AI** "make this stronger". | A change from "should" to "must" isn't made for you. |

## E. In the editor

| # | Do | Expect |
|---|---|---|
| E1 | Right-click in a topic. | **House Style: Lint Topic**, **House Style: Rewrite** and **House Style: Ask AI** once each. |
| E2 | Type "Please utilize the customer list." at the end of a paragraph and stop. | About a second later, the paragraph gets a margin bar. |
| E3 | Save (Ctrl+S) a topic with issues. | A notice with the number of issues. The saved file has no marker in it. |
| E4 | Start a build. | A notice with the number of issues; the build runs normally. |
| E5 | On the **Linting** tab of Settings, clear each option. | The matching behavior stops; the others still work. |

## C. Lint Project

| # | Do | Expect |
|---|---|---|
| C1 | Open any topic, **Lint Project**. | About 250 files linted in under a minute; one row per rule, most frequent first, each saying **Auto-fix**, **Needs you** or how many are Auto-fix; no AI cost. |
| C2 | Select a rule, **Change All…**. | A preview with files and counts; nothing changes until you confirm. |
| C3 | **Fix All (n)…**, then **Stop after this file** partway. | Files already changed keep their tracked changes; the rest are untouched. Flare stays responsive. |
| C4 | Select a rule, **Review One by One**, then Esc. | Only that rule's issues, then all rules again. |
| C5 | Select an issue in a file that isn't open, **Go To**. | The file opens with the text selected. |
| C6 | Right-click an issue. | **Go To**, **Open File**, **Copy File Path**, **Report False Positive…**. |
| C7 | Change an issue in another file, switch to it, press Ctrl+Z, and return to the pane. | The issue is back. |
| C8 | **By file**, then **By rule**. | The list switches; the choice is remembered next time. |

## R. Rules

| # | Do | Expect |
|---|---|---|
| R1 | **Rules**. Search for `click on`. | The Google word-list rule, with its examples, level and source. |
| R2 | Filter **State** to **Disabled**. | House Style's default changes are listed, each labeled **Default** with a reason. |
| R3 | **Disable Rule…** with a reason. Lint a topic. | The rule no longer appears, from the style rules or the AI. |
| R4 | **Enable Rule** on the same rule. | Offers other rules disabled with it. After the next lint, the rule appears again. |
| R5 | Resize the window, close it and open it again. | Same size. |

## D. Style guide and team

| # | Do | Expect |
|---|---|---|
| D1 | **Test Rules**. | Every rule table row checked against its example; failures listed with the row. The AI check covers only your team's rules unless **Include Google's rules** is selected. |
| D2 | **Import Rules > Mine this project** on the corpus. | Competing forms found (for example `customers` and `members`) with counts and sentences. |
| D3 | **Import Rules > Add notes or an old guide…**, load a `.docx`, **Extract rules**. | Suggested rules quote your words; **Edit rule…** and **Remove** work before **Add checked rules to the guide**. |
| D4 | Load a file that's still open in Word. | It loads, or a plain message says why. No crash. |
| D5 | Use a team guide whose `FlareAI/owners.txt` lists someone else, and whose folder is read-only for you. **Disable Rule…** on an issue. | The button reads **Request Rule Change…**; the request is sent; the issue shows **Request sent**. |
| D6 | As that writer, **My Requests**. | Your request with its state; **Withdraw request** works; **Import Rules…** is there. |
| D7 | As an owner, **Rule Decisions > Approve or decline…** on that request. | After the writer's next lint, a notice says approved or declined, with the note. |
| D8 | Ask for an exception for this project only, and have an owner approve it. | On the writer's next lint, the exception is written into the project's `overrides.ini`. |
| D9 | Make `owners.txt` unreadable for the writer. | Every change from that writer becomes a request. |
| D10 | Point Settings at a team guide folder you can't reach. | Linting uses Google's guide only, with a note that the team guide couldn't be read. |

## P. Reports

| # | Do | Expect |
|---|---|---|
| P1 | **Help > Report a Problem…**. | **Send** stays off until **What happened?** has text. **Preview** shows exactly what is sent. |
| P2 | Right-click an issue, **Report False Positive…**. | Three reasons; **Send** stays off until you pick one. **Include the surrounding sentence** is off. The issue stays in the list after sending. |
| P3 | Disconnect from the network and send a report. | The report is saved in **Documents\House Style Reports**, with **Open GitHub Issues** and **Open Folder**. |
| P4 | Open **Preview** with **Include diagnostic details** checked. | No topic text, no key, no email address and no folder paths. |
| P5 | Check **Send error reports automatically** on the **Advanced** tab. | Later error reports go in the background with a short notice. |

## O. Output and assists

| # | Do | Expect |
|---|---|---|
| O1 | **Topic Metadata** on any topic. | Description, keywords, synonyms, micro content and glossary drafts, each with Copy; nothing written into the topic. |
| O2 | Build the HTML5 target, then **Settings > Advanced > Export for LLMs…**. | `llms.txt`, `llms-full.txt` and one `.md` per topic. (Trial Flare scrambles output text; the files are still produced.) |

## U. Updates

| # | Do | Expect |
|---|---|---|
| U1 | **Settings > Advanced > Diagnostics…**. | Your version, "Automatic updates: on", and the last update check. |
| U2 | When a new release is out, wait for the notice "House Style X installs when you close Flare". Close Flare and start it. | No prompt before the notice. The new version runs, "Updated to House Style X" and **What's New** show. No administrator prompt. |
| U3 | Clear **Keep House Style up to date** on the **Advanced** tab. | Diagnostics says updates are off in Settings. |

## F. Headless bench (optional, needs the .NET SDK)

```
dotnet build tools/FlareAI.Bench
tools\FlareAI.Bench\bin\Debug\net48\FlareAI.Bench.exe --no-ai               # style rules and structure, free
tools\FlareAI.Bench\bin\Debug\net48\FlareAI.Bench.exe --sample 40 --rewrites 8   # AI review, about $1 on Sonnet
tools\FlareAI.Bench\bin\Debug\net48\FlareAI.Bench.exe summarize bench-out
```

Each run writes `bench-out/<run>/report.md`, `metrics.json`, `findings.jsonl` and per-author `feedback/*.jsonl`.
Options: `--model`, `--effort low|medium|high`, `--unit block|section`.

## What always counts as a bug

- Flare crashes or freezes.
- A change you didn't make appears in a topic, or a change isn't tracked.
- A variable, cross-reference, snippet or condition is lost or changed by an AI edit.
- An issue points at the wrong text.
- The key appears anywhere (logs, files, reports, screenshots of settings).
