# What's new in House Style

<!--
Shown in the What's new window after an update, on the first run of a new version, and from House Style > What's new.
One "## <version>" section per release (optionally "## 0.2.0 — October 2026"), newest first. Inside a section:
"### " headings, "- " bullets (indent wrapped lines by two spaces), plain paragraphs, and [text](https://...) links.
Write each entry from the writer's side, in short plain sentences. No arrows, colon joins or taglines.
New entries go in changes/<name>.md, not here: the nightly release adds them to the shipped copy (changes/README.md).
-->

## 0.3.4

- Closed the issues pane by mistake? Select Reopen Issues to bring back your last results without linting again.
- Close in the issues pane now closes it, also when the pane sits next to Content Explorer.
- Lint Project can leave files out. Right-click an issue and select Exclude File from Lint Project or Exclude Folder
  from Lint Project. Excluded Files at the top of the pane lists them, and Include Again lints them again.
- An issue that the AI review and your style rules both find is listed once, not twice.
- Help opens the House Style user guide, now online at gethousestyle.com/guide.
- Report a Problem has one box. Describe the problem or the change you'd like, and select Send.
- The last line of What's New is no longer cut off.
- Fix a rule that's already in your style guide. In Rules, select Edit Rule to change its wording or examples, or
  Delete Rule to remove it. You no longer have to edit the guide topic in Flare.

## 0.3.3

- House Style's GitHub project is now called house-style. Updates, Report a Problem and the release notes use the new
  address. Old links still work.

## 0.3.2

- Security fixes. Updates are now signed, and House Style installs only an update that we signed.
- Requests to style guide owners are safer. An approved project exception is added only for a request you sent, and
  only to the project you named.
- When House Style checks whether a team style guide in Git is up to date, it no longer runs programs from that
  guide's Git settings.

## 0.3.1

- The starter team rules are now general rules any docs team can use. If your team keeps its own style guide, nothing changes for you.
- Help links now go to gethousestyle.com.

## 0.3.0

### What you'll notice first

- New words. House Style lints a topic and lists its issues. For each one you choose Change or Ignore, or Disable Rule
  to stop the rule. These are the words Flare and other docs linters use.
- A shorter ribbon. Writers see the commands they use every day. Style guide owners also get a Style Guide group.
  Export for LLMs and Diagnostics are now in Settings > Advanced.
- A guided first lint. New writers get a short setup and a five-step practice on a sample project. To practice again,
  select Help > Start the Tour Again.
- Report a Problem and Report False Positive. Select Help > Report a Problem to tell us what went wrong. When a rule
  flags text that is fine, right-click the issue and select Report False Positive.

### Also new

- The issues pane is easy to read at Flare's narrow docked width.
- On a team style guide, My Requests shows your rule change requests and the owners' answers.
- Fix All makes every Auto-fix change in the topic at once.
- The installer turns House Style on in Flare for you.
- Error reports are sent only when you choose, and never include your topic text.

## 0.2.0

### The changes you'll notice first

- Setting up the gateway needed a walkthrough → now a three-step setup asks how you use AI and where your style
  rules are. New writers see it on first use; you can open it from Settings > Run setup again. Test connection
  checks the address and key, and a missing https:// or an extra slash is fixed for you.
- You wanted to jump from Check project to the problem → now Go to selects the text, and Open file opens the topic or
  snippet. Right-click a suggestion for the same commands.
- A long Check project list was hard to work through → now it's grouped by rule, most frequent first. Fix all shows
  the files and counts before it changes anything. Review one by one steps through a single rule.
- Turning off a rule didn't stop it → now it does, on the next check, without admin rights. Style rules on the ribbon
  lists every rule, with search and filters. Turn rules off, change their level, or turn them back on there.
- You wanted owners to control the team's rules → now the guide's owners.txt names them. Other writers choose Request
  a change…, and owners approve or decline in Tune style guide. You see the answer on your next check.

### Also new

- Updates install themselves. House Style checks for a new version once a day and installs it when you close Flare.
  This window shows what changed.
- Build style guide reads Word files (.docx) and Word web pages. Extract rules is the main button, and you can edit
  or remove a suggested rule before you add it. A file that's still open in Word no longer causes an error.
- Settings has four tabs: AI, Style guide, Checking and Advanced.
- With No AI chosen, earlier AI suggestions are hidden and the pane says "AI review is off".
- If you undo a fix with Ctrl+Z, its suggestion comes back in the pane.
- AI suggestions that change must, should or may, or a code term, are left for you to decide.
- Suggested alt text is 125 characters or fewer.
- AI costs less: reviews are reused after you apply fixes, and prompts are smaller. Validate style guide checks only
  your team's rules unless you include Google's.
- If your team's guide can't be read, House Style checks with Google's guide and tells you why.
- Errors are written to House Style's own log, and the Plugins list shows the real version number.

[Release notes on GitHub](https://github.com/abmorrill4/house-style/releases)
