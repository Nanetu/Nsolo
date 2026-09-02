# Nsolo: what remains

Updated 2026-09-02 after the panel-wiring pass. Verified against Unity 2022.3.62f3 in batch mode,
not just by reading files.

Companion docs: `UI_REBUILD_GUIDE.md` (how to tag a screen), `multiplayer-checklist.md` (the
AI→human plan, stale in the places noted in section 4), `online-wiring.md`, `hotseat-wiring.md`.

---

## 1. What the wiring pass changed

Every screen now runs off the **new** panel. Previously three screens were still driven by the old
one or by nothing at all.

| Screen | Now driven by | Was |
|---|---|---|
| Main menu | `Main menu` | unchanged |
| Mode | `ModePanelNew` | unchanged |
| Difficulty | `DifficultySelectionPanel` | unchanged |
| Profile | `ProfilePanelNew` | unchanged |
| Online | `OnlineModeNew` | unchanged |
| Lobby | `LobbyPanel` | unchanged |
| Pause | `PausePanelReactive` | unchanged |
| **Tutorial** | **`TutorialPanelNew`** | **nothing — the new panel was never tagged** |
| **Game over** | **`GameOverPanelNew`** | **old and new both claimed it; old won for labels** |

Edits applied to `SampleScene.unity` (13 in total):

- `TutorialPanelNew` — added the missing `NsoloPanel`, id `Tutorial`
- `TutorialPanelNew/Back` — `Back` → `TutorialClose`, matching the old panel's `CloseTutorial`
- `TutorialPanelNew/Start` — `DifficultyStart` → `TutorialStart`; it was starting a game instead
  of leaving the tutorial
- `GameOverPanel` (old) — `NsoloPanel` id released to `None`, and its nine element tags released,
  so the visible new panel is the one that gets written to
- `GameOverPanelNew/Rematch` — `MainMenuPlay` → `GameOverPlayAgain`; it called `ShowMode`
- `GameOverPanelNew/Main Menu` — `MainMenuHowToPlay` → `GameOverMainMenu`; it opened the tutorial
- `HUDRoot/Start` — `PauseResume` → `HudAction`. **This was the in-game START / HINT pill bound to
  `ResumeGame`.** Element tags bind regardless of whether their panel is tagged, and
  `replaceExistingClicks` had already cleared its real handler.
- `HUDRoot/Pause`, `HUDRoot/Undo` — were untagged, so they got no press feedback; now `HudPause`
  and `HudUndo`
- `MenuManager.aiBackgroundPanel` → `BackgroundPanelNew`, and the old `BackgroundPanel` switched
  off. Both were on at once, and the new art was wired to nothing.

### Verified

- Scripts compile with **no errors**
- `Nsolo → Check UI Wiring`: **all nine screens OK**, none left on an old screen
- Play mode boots: `NsoloUI: 9 tagged panel(s), 70 tagged element(s) bound`, **no** "nothing to
  bind" warnings, no exceptions, board spawns its 64 stones

A byte-identical backup of the pre-edit scene is at
`…/scratchpad/SampleScene.BACKUP.unity` for this session.

---

## 1b. Second pass

- **Tutorial copy written.** All seven body texts in `TutorialPanelNew` filled: how to play, the
  board, formation, sowing, landing outcomes, capture-restart, victory conditions. Written against
  the rules in `report-source-material.md` §1 and the voice of the in-game tips in
  `TutorialCoach.cs`. Plain language, short sentences, no mancala jargon.
- **HUD cards are static again.** `Button` and the inert `NsoloElement` removed from `Move`,
  `Player1`, `Player2` and their `Holder` / `Icon` children — seven objects that had been
  duplicated from the pause button and so carried `TogglePause`. Only `Pause`, `Undo` and `Start`
  are buttons on the HUD now.
- **Avatar moved onto the new Profile screen.** `AvatarCircle` (with its icon, monogram and
  button) and `AvatarPickerPanel` were re-parented from the old `ProfilePanel` into
  `ProfilePanelNew`, rather than rebuilt — this keeps `ProfileManager`'s `avatarPickerPanel`,
  `avatarPickerGrid` and `avatarOptionTemplate` slots valid, none of which are tag-resolved. The
  circle now uses `Profile holder.png`, sits where the `PlayerOne` placeholder was, and that
  placeholder plus the old `AvatarRing` are switched off. Tagged `ProfileAvatarButton` (411),
  `ProfileAvatarIcon` (409), `ProfileAvatarMonogram` (410), `ProfileCloseAvatarPicker` (412).
- **Quit** needed nothing — `Main menu/Quit` was already tagged `MainMenuQuit` and bound to
  `MenuManager.QuitGame`.
- **Pause deliberately has no HOW TO PLAY.** Tutorial is reached from the main menu instead.

Re-verified: compiles clean, all nine screens **OK**, `NsoloUI: 9 tagged panel(s), 74 tagged
element(s) bound`, no "nothing to bind" warnings, no exceptions.

---

## 2. What remains on the UI — needs your hand

- [ ] **Avatar circle needs positioning.** It was placed at the `PlayerOne` placeholder's position
      and scaled to match, but that was computed from numbers, not looked at. Nudge it in the
      editor. The old placeholder is still there, switched off, if you want its exact rect.

- [ ] **Menu music is unassigned.** `AudioManager.menuMusic` is empty, so `PlayMenuMusic()` plays
      silence. `gameMusic` is `Assets/Audio/Game Music.wav`; the unused candidates are
      `African3.mp3` and `African4.mp3`. One drag into the slot. (The empty `musicSource` /
      `sfxSource` slots are fine — `AudioManager` creates those at runtime, lines 87–98.)

- [ ] **Tutorial copy needs a read-through at size.** The words are in; whether each block fits its
      card without scrolling is a layout question I could not check.

- [ ] **Game over lost the difficulty label.** `GameOverDifficultyLabel` (803) existed only on the
      old panel. Optional — the checker does not require it.

- [ ] **Lobby cosmetics.** `LobbyHintLabel` (605) and `LobbyCopyCode` (607) untagged. Both are
      null-guarded, so they degrade silently. `LobbyShareCode` (608) works.

- [ ] **New art not yet placed:** `Globe.png`, `INVITE A FRIEND.png`, `ONLINE.png` are untracked in
      `Assets/Materials/Menu/Figma/` and used nowhere.

- [ ] **Then delete the old panels** — `WelcomePanel`, `DifficultyPanel`, `PausePanel`,
      `ProfilePanel`, `TutorialPanel`, `GameOverPanel`, `ModePanel`, `OnlinePanel`,
      `BackgroundPanel`. Only after play-testing. Deleting them also clears the 10 harmless
      "nothing picked" lines the wiring check still prints — one for the released `GameOverPanel`
      tag and nine for its released elements. Nothing else depends on any of them any more; the
      avatar was the last live thing on `ProfilePanel` and it has been moved out.

---

## 3. Test pass before the defense

Walk it once in the editor: menu → mode → difficulty → play → pause → resume → restart → quit to
menu → profile → tutorial → online → lobby → game over. Watch specifically for

1. the in-game **START / HINT** pill (it was bound to Resume until this pass),
2. **REMATCH** and **MAIN MENU** on the game-over screen (both were wrong),
3. the tutorial's **BACK** and **START** (both were wrong),
4. the menu **background** now that the new art is the one being driven.

---

## 4. Engineering backlog — parked until after the defense

Recorded so it is not rediscovered from scratch. None of it blocks the defense.

- **`multiplayer-checklist.md` is stale**: line 56's "App ID is still empty" is fixed
  (`AppIdRealtime` is set); line 51's "UI not built" is fixed; Phase 1's board-rotation unit test
  guards a mapping the design decided not to use.
- **Phase 0 refactor.** `GameController.cs` is 1724 lines, up ~70% from the ~1000 recorded, having
  absorbed online mode on top of turn flow, animation, undo, hints and stats. `MenuManager.cs` is
  1176. Phase 1's three `[~]` items all fold into this.
- **Phase 4 profile migration.** No `OpponentRecord` exists; `RecordGameResult(int difficulty, …)`
  is unchanged at `ProfileManager.cs:230`. `GameController.cs:1620-1633` gates recording behind
  `if (!hotSeat && !online)`, so **online matches record no result at all**. Deliberate — there is
  no axis to file them under — but it means online is invisible to the profile.
- **Phase 3 leftovers.** Rejoin / turn-timeout and rematch, both deliberately out of scope.

A false alarm worth not re-investigating: the lobby START button's inspector slot is empty by
design. `id: 606` is tagged in the scene and `OnlineFlowController.cs:178` resolves it at runtime.
Empty slots are normal throughout this layer.
