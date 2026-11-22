# Verification

## Current build

- Version: **0.7.30**
- SHA-256: `5822BDF274950B7A5983432D9FA4D55BE81A97AC995B7E30A381ED7D40F7141C`
- Compiles successfully and was installed with the exact candidate hash verified.
- Live Computer Use confirmed the launcher moved from screen y=1374–1416 to y=1303–1345 at 2560×1440 (about 71 screen pixels higher), leaving the bottom action-row area clear. Clicking it still opens Coach.
- The original cropped report did not identify its Apply/Cancel dialog, so that exact page could not be reproduced. Position and click behaviour were verified; exact-dialog clearance remains unverified.
- A visible bottom-row Expand control had a 20-pixel gap below the raised launcher. FM was closed normally without saving; the current save's before/after SHA-256 matched. One instruction row was accidentally toggled while searching for the dialog and immediately undone before quitting.

## Previous full live verification

- Version: **0.7.29**
- SHA-256 of the live-tested release DLL:
  `B7F63BD5396EFB7CFC77380878425AAE66AEDC45889464E762298C24D65B27E9`
- Windows, FM27 and BepInEx 6 IL2CPP x64.

The publication cleanup relocated source and updated build/test paths. Rebuilding from the cleaned layout produced the same SHA-256 as the live-tested release DLL, and all 169 current-source checks passed.

## Offline coverage

The current harness compiles the full plugin and runs 169 checks covering session reset, tactic replacement and identity, scoped caches, stale asynchronous replies, input text, registered-squad filtering, snapshot completeness, instruction formatting, opponent-context policy and portrait transparency.

These tests use substitutes for game bindings. Passing does not prove that a native FM provider returns the expected data after a game update.

## Live checks completed during development

- Registered available players, attributes and current club-season statistics were compared with FM screens.
- Own-team and opponent General Performance values were compared with the same season and competition in FM.
- Formation, mentality, team instructions, both possession-phase roles and all 17 effective player-instruction groups across 11 positions were checked for the two saved tactics in the test career.
- A slot switch during a read discarded the old payload; clean reads reached two-plan coverage after both plans had been opened.
- Cached inactive-plan details remained excluded from current factual AI advice.
- Unicode editing, Enter submission, context-aware questions, profile clearing, normal/expanded cards, debug controls and all eleven portrait frames were exercised through Computer Use.
- Opponent advice was tested before report navigation: it gave an initial plan, accepted that exact opposition roles are unknown, and separated observations from conditional tactics.
- The corrected prompt distinguished engagement height from defensive-line height and checked whether suggested settings were already selected.
- The final fresh-chat reply quoted the compared figures accurately. Its phrase “look even” was still a loose qualitative summary; statistical interpretation is not guaranteed.
- FM was closed normally after the final test, and the current save's hash was unchanged.

## Remaining limits

- Only the selected tactic is automatically readable through the tested native providers. Open other saved plans once per session. Three-plan inventory logic is covered offline; a three-plan career has not been verified live.
- Startup time depends on squad size and the installed game; about 88 seconds was observed in the test career.
- Header dragging could not be verified through the Computer Use helper: its drag gesture delivered no pointer-down event, although normal header clicks reached the handler. Physical-mouse dragging remains unverified.
- Internal game bindings and model-generated advice can change. Recheck data and behaviour after game or model changes.

Full development logs, native dumps, save backups and untrimmed screenshots are not part of the public repository.
