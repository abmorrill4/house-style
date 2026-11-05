---
title: Publish the style guide to Confluence
description: Publish your team guide to Confluence Data Center so people who do not use Flare can read it.
sidebar:
  order: 5
---

This topic is for style guide owners. A PowerShell script publishes your team's guide to Confluence Data Center 10, one page per topic, so people who don't use Flare can read it. The Flare guide stays the source. Don't edit the Confluence pages: the next publish overwrites them.

## Before you start

1. Download `publish-guide-to-confluence.ps1` from the `scripts` folder of the House Style repository on GitHub. Right-click it, select **Properties**, select **Unblock**, and then select **OK**.
2. In Confluence, create a personal access token: select your profile picture, and then **Settings > Personal Access Tokens**. The token's user must be able to view the parent page and add and edit pages in the space.
3. Create an empty parent page for the guide. Note its page ID: it's the number in the page's address, or in **Page Information**.
4. Open Windows PowerShell in the folder that has the script, and set the connection. Don't save the token in a shared file.

```
$env:CONFLUENCE_BASE_URL = "https://wiki.example.com/confluence"
$env:CONFLUENCE_PAT = "<your token>"
$env:CONFLUENCE_PARENT_ID = "123456"
```

## Publish

1. Try it without sending anything. Replace the path with your team's guide folder:

```
powershell -ExecutionPolicy Bypass -File .\publish-guide-to-confluence.ps1 -GuideProject "C:\path\to\team guide" -Folders Company -WhatIf
```
The script lists the pages and saves the converted pages to a `confluence-preview` folder.
2. Publish to a test space first: run the same command without `-WhatIf`, with a parent page ID from a test space.
3. Open a page. It starts with an "Edited in Flare" note, and links between guide pages work.
4. Set `CONFLUENCE_PARENT_ID` to the real parent page, and run the command again.

Run it after each change to the guide. Only changed pages are updated. A topic you remove from the guide stays in Confluence with the label `flare-orphaned`. Delete it there when you're ready.

To publish Google's rules too, leave out `-Folders Company` and point `-GuideProject` at a guide that includes the Google folder.
