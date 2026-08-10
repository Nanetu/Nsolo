# Hot-seat wiring contract

**Status: all of this is done.** Authored into `SampleScene.unity` on 2026-08-08 and verified by a
headless Unity run — scene imports clean, scripts compile, and every reference below was confirmed
bound (not merely present) by walking `SerializedObject` in batch mode.

Kept as a record of what is wired to what, and as the reference if any of it needs rebuilding.
Every slot is defensive: a missing one degrades rather than crashes — no flipper means no rotation,
no modal means no handover card.

---

## 1. Two new components on `GameSystems`

`GameSystems` already carries `TutorialCoach` and `AudioManager`. Both new components go on the
same object, for the same reason: it is always active, so their `Awake` always runs, and they
drive their panels by reference rather than living inside them.

> **Trap worth naming.** If you put `PassDeviceModal` on the panel it drives and that panel starts
> inactive in the scene, `Awake` never runs, `Instance` stays null, and every handover silently
> skips the card with only a console warning. Put it on `GameSystems`.

### `BoardFlipper`

| Slot | Wire to | Notes |
|---|---|---|
| Board Camera | `Main Camera` | Falls back to `Camera.main` if empty |
| Pivot Override | *leave empty* | Empty means it averages the 32 `Hole_r_c` positions, which is what you want |
| Flip Seconds | `0.7` | Range 0.3–1.5 |
| Enable Breadcrumb Logs | off | |

### `PassDeviceModal`

Build its panel by duplicating `Canvas/TutorialTipPanel` — it already has the `CanvasGroup`,
backdrop and card the script expects. Rename the copy `PassDevicePanel` and **delete the
`TipGotItButton` child**: this card is dismissed by tapping anywhere, not by a button.

| Slot | Wire to |
|---|---|
| Panel Root | `PassDevicePanel` |
| Backdrop | `PassDevicePanel/TipBackdrop` |
| Card | `PassDevicePanel/TipCard` |
| Canvas Group | the `CanvasGroup` on `PassDevicePanel` |
| Title Text | `PassDevicePanel/TipCard/TipTitle` |
| Body Text | `PassDevicePanel/TipCard/TipBody` |

Title and body text are written by the script at show time, so whatever the copies say in the
scene does not matter.

---

## 2. New slots on existing components

### `GameController`

| Slot | Wire to |
|---|---|
| Board Flipper | the `BoardFlipper` on `GameSystems` (auto-found if empty) |

### `MenuManager`

| Slot | Wire to | Notes |
|---|---|---|
| Mode Panel | `ModePanel` | new — the vs Computer / vs Human screen |
| Vs Computer Selection Highlight | `ModePanel/VsComputerHighlight` | selected-state overlay on the card |
| Vs Human Selection Highlight | `ModePanel/VsHumanHighlight` | selected-state overlay on the card |
| Mode Continue Button | `ModePanel/Continue` | held disabled until a card is picked |
| Ai Background Panel | `BackgroundCanvas/BackgroundPanel` | the existing one, with the Undo pill |
| Human Background Panel | the `Background - local` panel | no Undo pill |
| Undo Button Object | `Canvas/HUDRoot/Undo` | the button object itself, not the panel |

**Why Undo needs its own slot.** The HUD buttons have transparent images — the pills they appear
to sit on are painted into the background art. So switching the background alone would leave a
live, invisible Undo button floating over bare art in two-player play. It gets switched off with
the background that draws it.

---

## 3. Button rewiring

The Mode panel **selects, then confirms** — the art has a CONTINUE button, so the two cards set a
selection rather than navigating on tap. Same shape as the difficulty panel.

| Button | Change onClick to | Was |
|---|---|---|
| `WelcomePanel/Play` | `MenuManager.ShowMode` | `MenuManager.ShowDifficulty` |
| `ModePanel/VsComputer` | `MenuManager.SelectVsComputer` | new |
| `ModePanel/VsHuman` | `MenuManager.SelectVsHuman` | new |
| `ModePanel/Continue` | `MenuManager.ContinueFromMode` | new |
| `ModePanel/Back` | `MenuManager.BackToWelcome` | new |

Optional but consistent: `DifficultyPanel/Back` currently calls `BackToWelcome`, which now skips a
level. Pointing it at `MenuManager.ShowMode` makes Back walk back up the flow it came down.

Nothing else changes. `vs Computer` lands in the existing difficulty panel and the single-player
path from there is untouched.

---

## 4. What the second background needs to account for

- **No Undo pill.** Its button is switched off in this mode.
- **The bottom pill reads START, then FORFEIT.** Same object as single-player's START/HINT pill
  (`Canvas/HUDRoot/Start`), so it must sit in the same place on both backgrounds. It briefly reads
  `CONFIRM?` between the two forfeit presses, so the pill needs room for eight characters.
- **The two score labels are Player 1 and Player 2, not Player and AI.** The numbers are already
  correct — `HumanScore` is player 1's stones and `AIScore` is player 2's — but if the AI
  background paints the word "AI" next to one of them, the human background should not.
