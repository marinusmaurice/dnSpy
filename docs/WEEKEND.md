<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Weekend branches (2026-10-30 to 2026-11-01)

Unattended weekend work, for the owner to review and release in order (fast-forward `master` and `0000_Update`, tag). Each branch stacks on the one above it.

| Branch | Version | What it does | CI | Status |
|---|---|---|---|---|
| `7.4.1.0/readme-short-title` | (part of 7.5.0.0) | Short title bar, new README. Not released on its own (owner's decision). | see GitHub Actions | merged into 7.5.0.0 |
| `7.5.0.0/time-travel-debugging` | 7.5.0.0 | Time-travel debugging (Debug History of stops, Step Back/Forward, value diff). Step a (design, `docs/TIME_TRAVEL_DEBUGGING.md`), step b (recording engine + options page, off by default) and step c1 (Debug History window, Debug → Step Back/Step Forward/Return to Live) done; not click-tested. | ✅ green for 3a20bb0d2 (2026-10-30 19:25) | **not ready**: needs c2 (historical statement marker, toolbar buttons) and the owner's click-test (steps in `docs/TIME_TRAVEL_DEBUGGING.md`) |
