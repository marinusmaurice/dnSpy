<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Weekend branches (2026-10-30 to 2026-11-01)

Unattended weekend work, for the owner to review and release in order (fast-forward `master` and `0000_Update`, tag). Each branch stacks on the one above it.

| Branch | Version | What it does | CI | Status |
|---|---|---|---|---|
| `7.4.1.0/readme-short-title` | (part of 7.5.0.0) | Short title bar, new README. Not released on its own (owner's decision). | see GitHub Actions | merged into 7.5.0.0 |
| `7.5.0.0/time-travel-debugging` | 7.5.0.0 | Time-travel debugging (Debug History of stops, Step Back/Forward, value diff). Step a (design, `docs/TIME_TRAVEL_DEBUGGING.md`) and step b (recording engine + options page, off by default, not click-tested) done. | ✅ green for 75730879d (2026-10-30 16:18) | **not ready**: needs the UI (c): Debug History window, Step Back/Forward |
