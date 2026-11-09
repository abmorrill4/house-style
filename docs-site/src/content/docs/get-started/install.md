---
title: Install and update
description: Install House Style for MadCap Flare 2026, and how it keeps itself up to date.
sidebar:
  order: 2
---

You need Windows and MadCap Flare 2026. The first install asks for administrator rights once.

## Install

1. Download [HouseStyle-Setup.exe](https://github.com/abmorrill4/house-style/releases/latest/download/HouseStyle-Setup.exe) from the project's GitHub releases page.
2. Close Flare.
3. Run it, and approve the administrator prompt.
4. Start Flare. The installer turns House Style on for you, so the **House Style** tab is on the ribbon and setup opens. If the tab is missing, select **File > Options > Plugins**, select House Style, and then select **Enable**.

If you have more than one version of Flare, the installer uses the newest. House Style needs Flare's Ribbon interface (**View > Interface**).

To upgrade a copy you installed by hand, run **HouseStyle-Setup.exe** from the new download. Your settings and rule changes are kept.

## Updates

House Style updates itself. Updates work only for a copy installed with **HouseStyle-Setup.exe**.

1. Once a day, House Style checks GitHub for a new version.
2. When there is one, House Style downloads it and checks it. A notice says it installs when you close Flare.
3. When you close Flare, the update installs, without an administrator prompt.

The next time you start Flare, **What's New** lists the changes. If a download fails, House Style tries again the next day. To open it again, select **House Style > Help > What's New**. If an update can't install, you stay on your version, and a notice says why.

To turn updates off, select **House Style > Settings**, and on the **Advanced** tab clear **Keep House Style up to date**.

**Diagnostics…** in **House Style > Settings > Advanced** shows your version and when House Style last checked for updates.
