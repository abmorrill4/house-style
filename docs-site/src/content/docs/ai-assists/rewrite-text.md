---
title: Rewrite text
description: Simplify, shorten or reword paragraphs, with every change tracked.
sidebar:
  order: 1
---

Select the paragraphs to rewrite, and then select **House Style > Rewrite**. To give an instruction, such as shorten to two sentences, select **House Style > Ask AI** instead.

The arrow next to **Rewrite**, and the right-click **Rewrite** item, list **Simplify** and **Shorten**. Each one runs without an instruction. A team can change their wording in the `FlareAI/prompts` folder of the team style guide, in `preset-simplify.md` and `preset-shorten.md`.

The AI changes only the wording. It can't change variables, cross-references, snippets, conditions, or formatting. The result arrives as word-level tracked changes in one undo step. Press Ctrl+Z to take all of it out.

Before it applies anything, House Style checks the AI's result against the style rules. If the rewrite still breaks a rule, the AI gets one more try, with the remaining problems listed.

## Give an instruction

Select the text and then select **House Style > Ask AI**. Choose one of these:

| Choice | What it does |
|---|---|
| **Ask a question** | Answers a question about the text. It changes nothing. |
| **Make a change** | Changes the text the way you describe. With nothing selected, it applies to the whole topic. |
| **Insert at the cursor** | Writes new text where your cursor is. See [Write new text at the cursor](/docs/ai-assists/write-new-text/). |
| **Have a conversation** | Lets you ask, refine and then apply. See [Ask AI as a conversation](/docs/ai-assists/ask-ai-conversation/). |

## When the structure must change

Some edits need a new structure, such as splitting one step into two or turning a paragraph into a list. When the AI says the instruction needs that, the result window offers **Restructure instead…**. House Style checks the new structure before it applies anything. If the result would drop or change a variable, cross-reference, snippet or condition, House Style refuses it and nothing changes.

House Style can reword a paragraph that has pending tracked changes, but it can't restructure it. Accept or reject those changes first.

## What it costs

Rewrite and Ask AI use your AI key. See [Models and cost](/docs/settings-and-privacy/models-and-cost/).
