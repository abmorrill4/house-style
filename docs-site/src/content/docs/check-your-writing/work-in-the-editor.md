---
title: Work in the editor
description: Markers, the right-click menu, and checks that run while you type, save and build.
sidebar:
  order: 3
---

Besides the **House Style** tab and the issues pane, the plugin works where you type. None of these features ever stops you from typing, saving, or building, and each one can be turned off in **House Style > Settings**.

## Right-click menu

Right-click in a topic to find **House Style: Lint Topic**, **House Style: Rewrite**, and **House Style: Ask AI**. They do the same as the buttons on the **House Style** tab, for the text you right-clicked.

## Turn AI on or off

The **AI On** or **AI Off** button in the AI group of the **House Style** tab turns AI on or off. It does the same as **No AI** on the **AI** tab of Settings. With AI off, linting uses the style rules only, and nothing is sent or charged. If you turn AI on and no key is set, Settings opens on the **AI** tab so you can add one.

## Markers

A paragraph with open issues gets a colored bar in the margin and a light tint: red for errors, amber for warnings, and blue for suggestions. The markers follow the issues pane: when you change or ignore an issue, they update. They appear only in the editor and are never saved into the topic.

## Linting as you type

About a second after you stop typing, the plugin lints the topic again and updates the markers and the issues pane. This check uses no AI and costs nothing. The AI review still runs only when you ask for it.

## Linting on save

When you save a topic that has style issues, a short notice shows how many, with a **Show Issues** link. The notice ends with "Saved anyway." The topic is saved either way.

## Reports when you build

When a build starts, the plugin lints every topic and snippet in the project and shows a notice with the number of style issues and a **Show Issues** link, which opens Lint Project. With no issues, the notice says so and has no link. The build isn't delayed.
