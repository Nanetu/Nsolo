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

## 1c. Third pass — HUD captions, Photon versioning, game-over audit

- **Score-box captions now written per mode.** They had been *painted into the background art* —
  a different image per mode with COMPUTER or PLAYER 1 already lettered on — so the rebuilt HUD's
  real text objects had nothing writing to them. `UIManager.SetOpponentName` was online-only and
  there was no player-side field at all. Replaced with `SetSeatNames(GameMode, opponentName)`:
  COMPUTER / YOU, PLAYER 1 / PLAYER 2, and the opponent's Photon nickname / YOU. New ids
  `HudOpponentNameLabel` (1005) and `HudPlayerNameLabel` (1006), tagged on
  `HUDRoot/Player1/OpponentNameText` and `HUDRoot/Player2/PlayerNameText`. Verified in play mode
  for all three modes.

- **Photon: cross-version play was broken by a settings field, not by the UI.** Photon partitions
  clients by `AppVersion` — different values connect fine and see none of each other's rooms,
  which surfaces as "room not found" for a code that exists. PUN derives it from
  `PhotonNetwork.GameVersion`, which `ConnectUsingSettings` reads from
  `PhotonServerSettings.AppVersion`. That field was **empty at `41dfeb6`** (the v3 online release,
  and what the existing APK was built from) and became **`1.0` at `3ae3d44`** — the "interactive
  feel" UI commit. So the APK speaks `_2.55` and current builds speak `1.0_2.55`.

  `PhotonMatchTransport` now pins `GameVersion` to a protocol constant, `nsolo-net-1`, immediately
  after connecting. Bump it only when `NetProtocol`'s event codes or payload shape change — never
  for a version number or a UI rebuild. **Both devices need one fresh build to pair again**; after
  that, UI changes can never separate players.

- **Game over, audited in play mode** by invoking `HandleGameOver(1, 12, 8)` and reading back every
  slot. The event path itself is fine (`OnEnable` plus all four start paths subscribe). What
  actually resolves:

  | Field | Resolves to | Result |
  |---|---|---|
  | `gameOverTitleText` | `GameOverPanelNew/Winner` | "VICTORY" ✓ |
  | `gameOverTimeText` | `GameOverPanelNew/Time` | "00:00" ✓ |
  | `gameOverSummaryText` | `GameOverPanelNew/Score` | "You: 12 - 8" ✓ |
  | `GameOverCapturesLabel` | `GameOverPanelNew/Captures` | ✓ |
  | `GameOverRelayLabel` | `GameOverPanelNew/Relay` | ✓ |
  | `finalPlayerCapturedText` | **null** | no `801` anywhere |
  | `finalAiCapturedText` | **null** | no `802` anywhere |
  | `gameOverDifficultyText` | **old** `GameOverPanel/Difficulty` | written but inactive |
  | `gameOverVictoryCountText` | `Winner/Victory Count` | written but **switched off** |

  So five of nine populate. The four that do not are structural, not wiring: the new panel has no
  object for them. Listed in section 2 as decisions rather than bugs.

## 1d. Fourth pass — the press glow

The glow is a `PressLayer` GameObject built at runtime in `UIPressFeedback.Awake()`. It is not in
the scene, which is why there is nothing to find and nothing to scale by hand.

It was never the wrong size — it stretched to the button's rect exactly. Two other things were
wrong, both caused by how the Figma buttons are sized. **Every** button in the game has a
`sizeDelta` of 160×30 and gets its real size from a non-uniform `localScale`, typically about 4×
across and 6× down.

- **Corners smeared.** The whole point of the 9-sliced sprite is to hold the corner radius constant
  while the middle stretches, and that only works when the button is stretched by *sizeDelta*.
  Scaling the parent instead scaled the corners with it — 4× horizontally, 6× vertically — so the
  rounded ends came out as lopsided ovals overhanging the artwork. `FitLayer()` now gives the layer
  the button's *effective* size and the inverse of its scale. They cancel to the same on-screen
  rectangle, but the border is drawn in unscaled units. The press animation still rides on top.
- **Every button got a pill.** `UIShapes.For` judged shape from `rect.size`, which is 160×30 for
  everything — a 5.3:1 sliver — so it always returned `Pill`. It now measures the scaled size, so
  the near-square mode and difficulty cards (ratios 1.06–1.35) correctly get `Card`.

Verified in play mode across 18 buttons: layer size matches button size to the pixel in every case,
and the sprite is now `NsoloCard` for the cards and `NsoloPill` for the pills.

If you still want the glow tighter or looser than the button, the knobs are on `UIPressFeedback`:
`pressAlpha` (brightness, 0.14), `pressedScale` (how far the button shrinks, 0.97), `seconds`
(0.09), and `idleAlpha` for a slow shimmer on one hero button per screen. The corner radius itself
is `UIShapes.Rounded(30, …)` for pills and `(16, …)` for cards.

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

- [ ] **Game over: four fields have nowhere to go.** Decisions, not bugs — the new panel simply has
      no object for each:
      - `Victory Count` exists but is a child of `Winner` and **switched off**. Turn it on and
        position it if you want the lifetime win count; it was off on the old panel too.
      - **Difficulty** (`EASY` / `2 PLAYER` / `ONLINE`) has no label on the new panel, so the text
        goes to the old one. Add a text object and tag it `GameOverDifficultyLabel` (803) if wanted.
      - **Separate player / opponent score boxes** (`801` / `802`) do not exist on the new design;
        the combined `Score` line covers the same information. Nothing to do unless you want them
        split.

- [ ] **Rebuild the APK on both devices before demoing online.** The version pin below means the
      existing APK and any new build cannot see each other's rooms. One fresh build on both, once.

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

---

## Cleanup pass — 2026-09-03

Verified against Unity 2022.3.62f3 in batch mode: scripts compile with no errors and no warnings,
`Nsolo → Check UI Wiring` reports **no problems**, and play mode boots clean
(`NsoloUI: 9 tagged panel(s), 83 tagged element(s) bound`, no exceptions, board spawns).

### No script references a retired screen any more

Every controller used to reach its screens through Inspector slots, and a startup pass then
overwrote those slots with the tagged panel where one existed. That scaffolding was for a rebuild
happening one screen at a time. It had outlived its purpose and become the problem: **every slot in
the scene still held the old panel**, so the Inspector said one thing and the running game did
another, and a slot nobody had re-dragged looked exactly like one that had been.

Screens and their contents are now looked up by what they are. Slots removed:

| Component | Slots removed | Was pointing at |
|---|---|---|
| MenuManager | 34 | `WelcomePanel`, `ModePanel`, `DifficultyPanel`, `ProfilePanel`, `PausePanel`, `GameOverPanel`, `TutorialPanel` and their labels |
| ProfileManager | 9 | `ProfilePanel`'s eight labels and its name box |
| OnlineFlowController | 11 | `OnlinePanel`, and the lobby's labels and buttons |
| UIManager | 2 | `hudCanvas` and `normalColor`, both unread |

MenuManager keeps three slots (`gameController`, `gameplayRoot`, `onlineFlow`), ProfileManager keeps
its avatar and achievement **content**, and UIManager keeps its HUD slots as the fallback behind the
new tags. The retired panels remain in the hierarchy with their tags released to None.

### The background swap is gone

`ApplyGameplayChrome` switched between three background images, one per mode. That was right while
each background had its captions and its Undo pill painted into the artwork — the picture *was* the
HUD. It stopped being right when the HUD became real objects over one background: the two-player and
online modes switched `BackgroundPanelNew` **off** and an older picture **on**, leaving the new HUD
laid out over art it was never measured against. That is the layering both two-player modes showed.

It now sets one thing: whether there is an Undo button. (Which never worked either — `undoButtonObject`
was empty, so Undo stayed live and invisible in two-player games. It goes through `HudUndo` now.)

### Scene fixes

- `HUDRoot/Time/Time` — the static "TIME: " caption — was tagged `HudPlayerNameLabel`, the same id as
  the real name label. Two objects, one id: the mode captions written at every game start had an even
  chance of landing on the clock's caption. Released to None.
- The HUD's five unlabelled parts got ids of their own and were tagged: status, clock, both score
  figures, last move. `Check UI Wiring` now covers the HUD.

### Online

- **The disconnect freeze is fixed.** `OnlineFlowController` was subscribed to the transport's
  ending ahead of `GameController` and tore the match down first — which unhooked the controller
  from the event it was waiting for, so the board was never told. The "opponent left" dialog went up
  over a live game still in the opponent's turn, and dismissing it to look at the final position
  left a board that answered every tap with "wait for your opponent to move". It calls
  `GameController.EndOnlineMatch` before cleaning up now; the call is idempotent.
- **Tips are held back online** (`TutorialCoach.Suppressed`). A modal tip sets `Time.timeScale` to
  zero, which stops this device's clock while the opponent's runs on — and stops PUN dispatching, so
  the opponent's moves queue up unseen. Suppressed tips are not marked as shown, so they are still
  waiting the next time the player is offline.
- **The screen is held awake for the length of an online session.** Photon holds the connection for
  five minutes in the background on purpose, but many Android phones drop Wi-Fi outright when the
  display sleeps, and no timeout survives the socket going away.
- **Online is locked until one game has been finished offline** (`ProfileManager.HasPlayedOffline`,
  backfilled for existing saves). Tapping the card explains why and offers the computer.

### Still open

**Reconnect.** `PlayerTtl` and `EmptyRoomTtl` are still zero, so a player the server drops is gone
for good and their seat with them. Holding the seat open and letting them rejoin needs the host to
rebroadcast the board on arrival — `NetworkMatch` already has the message for it — plus a "waiting
for your opponent" state with a grace period. Untested territory that wants two real devices.
