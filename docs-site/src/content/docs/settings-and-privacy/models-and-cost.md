---
title: Models and cost
description: Which AI model does which task, what it costs, and how a team sets models.
sidebar:
  order: 2
---

Style checks, structure checks and Lint Project use no AI and cost nothing. AI costs money only when you use an AI feature: the AI review in Lint Topic, Rewrite, Ask AI (a question, a change, new text at the cursor, or a conversation), Write an Introduction, Rewrite… on an issue, Topic Metadata, Add Alt Text, and the AI steps in Import Rules, Rule Decisions and Test Rules.

## Which model does what

| Task | Tier | Default model and effort |
|---|---|---|
| AI review, Rewrite, Ask AI, Insert at the cursor, Write an Introduction, conversations | Standard | Claude Sonnet 5.5, medium |
| Alt text, rules from notes | Standard | Claude Sonnet 5.5, medium |
| Rewrite… on an issue, Topic Metadata | Fast | Claude Haiku 5.5, medium |
| Draft a change with AI (owners) | Deep | Claude Opus 5.5, high |

Leave the models on **Default** unless your style guide owner says otherwise. Sonnet changes only what each task calls for. Haiku can drop words in rewrites, and Opus can add details the text doesn't have.

## What it costs

An AI review costs a few cents per topic, more for long topics. Saved reviews are reused at no cost.

**Diagnostics…** in **House Style > Settings > Advanced** shows the models in use and, for this session, the AI calls and estimated cost per task.

## Set models for the team

A style guide owner can set models for every writer in the guide's `FlareAI/models.json`. The team file wins over each writer's Settings.

```
{
  "tiers": { "fast": { "model": "claude-haiku-5-5", "effort": "medium" } },
  "tasks": { "review": "standard" },
  "review_unit": "section"
}
```

- `review_unit`: `section` (the default) reviews a short topic, or one heading's worth of a long one, at a time. `block` reviews one paragraph, list item or table row at a time. Sections cost less and run faster.
- `always_packs`: the groups of rules every review includes. The default is Headings, UI, Lists and Steps. Other groups, such as the Code rules, are sent only for sections that need them.
