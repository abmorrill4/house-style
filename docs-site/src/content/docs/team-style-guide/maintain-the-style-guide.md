---
title: Maintain the style guide
description: Write rules, change Google rules, say where a rule applies, and test the guide.
sidebar:
  order: 2
---

This topic is for style guide owners. The style guide is a Flare project. Your team's rules are in `Content/Resources/StyleGuide/Company`. Google's rules come with House Style. Don't copy or edit them: change them from your Company folder.

To set up a guide for a team, see [Set up a team style guide](/docs/team-style-guide/set-up-a-team-style-guide/).

## Write rules

The **About this style guide** topic in the Company folder explains the format. In short:

- A bullet is a rule for the AI. Add nested **Bad:** and **Good:** items so it can be tested. Paragraphs are context, not rules.
- A table with the headers **Avoid**, **Use**, **Severity**, **Why** and **Example** becomes rules with exact fixes.
- A table with the headers **Rule**, **Setting** and **Reason** disables rules or changes their level. House Style writes these rows for you when you use **Disable Rule…**.

## Change a Google rule

Select **Disable Rule…** in the issues pane, or use **Rules**. Choose the team or this project only, the new level, and why. For the team, House Style writes the change into the guide's **Changes to the Google guide** topic. A disabled rule is used by neither the linter nor the AI. See [Browse and change style rules](/docs/check-your-writing/browse-and-change-rules/).

House Style ships with some Google rules disabled or changed for help topics, such as curly quotation marks and "this page". Each one is labeled **Default** in **Rules**, with its reason. You can enable any of them again.

## Say where a rule applies

A rule applies to every topic unless you tag it with Flare condition tags:

- **Content** tags, such as Steps, Code, Numbers or UI, limit a rule to sections with that content.
- **Product** tags limit a rule to products. The guide's Product tag set lists the products it covers. Rules for other products never apply.

## Check the guide

After you edit the guide, select **House Style > Test Rules**. It tests each rule table against its examples and finds rules that contradict or repeat each other. With AI, it also checks your team's written rules against their Bad and Good examples. To check Google's rules too, select **Include Google's rules**. That takes longer and costs more.

## Name your readers

A table headed **Role names** lists the words your team uses for its readers, such as *administrators* or *editors*. Put it in any topic of your guide.

| Role names |
|---|
| administrators |
| editors, reviewers |

- Put one or more names in a row. Separate names in a row with a comma or a semicolon.
- A name starts with a letter and can have letters, spaces, apostrophes and hyphens, up to 40 characters. House Style leaves out any other name and says so in the guide's check.
- A guide lists up to 20 names. Repeats are ignored.
- If both your guide and the project list role names, the last one that lists any wins. They aren't merged.

The starter rules that say to name the reader by role show your names in the issue's message, and the AI suggests them. Without a Role names table, the rules still work and name no roles.

## Build rules from evidence

Select **House Style > Import Rules** to build rules from your content, notes, old style guides and PDFs. See [Import rules](/docs/team-style-guide/import-rules/).

## Tune rules from review decisions

Select **House Style > Rule Decisions**. It collects the team's review decisions and lists rules that writers disagree on, rules they often ignore, edits several writers keep making, and requests. Each comes with its evidence. **Draft a change with AI…** rewrites a rule from that evidence as tracked changes in the guide.

Rule Decisions lists a rule only when enough writers made decisions on it, so one writer's preference doesn't change a shared rule.
