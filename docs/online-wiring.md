# Online (Photon PUN2) wiring contract

**Status: built and wired.** Authored into `SampleScene.unity` on 2026-08-13 and verified by a
headless Unity run — scripts compile, the scene imports clean, and every reference below was
confirmed *bound* (not merely present) by walking `SerializedObject` in batch mode. Button `onClick`
targets were checked separately.

**What is not verified: layout.** The batch check proves structure and wiring only. Nothing in it
looks at where anything sits on screen — see §7 for what to eyeball in the Game view.

Kept as a record of what is wired to what, and as the reference if any of it needs rebuilding. Same
shape as `hotseat-wiring.md`: every slot is defensive. A missing modal logs a warning and degrades —
it never deadlocks the flow it belongs to. The one deliberate exception is the forfeit modal: with
nothing wired, forfeit refuses rather than conceding, because a player who cannot forfeit is
inconvenienced and one who forfeits by accident is not.

---

## 0. Photon setup — done

PUN 2.55 is installed and `AppIdRealtime` is set in
`Assets/Photon/PhotonUnityNetworking/Resources/PhotonServerSettings.asset`. Without it the code
still compiles and runs, but every connection attempt fails and lands on the Room Not Found modal —
worth remembering if this project is ever cloned to a machine without the setting, or if the app is
deleted from the Photon dashboard.

PUN's asmdefs do not set `autoReferenced: false`, so `Assembly-CSharp` picks them up automatically —
the game scripts need no asmdef of their own.

The free tier caps concurrent users at 20, which is plentiful for testing and a defence demo.

Two settings that matter before building to a device, both set 2026-08-14:

- **`FixedRegion: za`.** Photon rooms are **scoped to a region**, and with this empty each client
  pings and picks its own. A PC on WiFi and a phone on mobile data can land in different regions,
  and then a perfectly valid code returns "Room not found" while everything looks correct. Pinning
  both clients to one region removes the whole failure mode. Change it if you test from elsewhere —
  the cost of a wrong region is latency, the cost of *no* region is silent mismatches.
- **`ForceInternetPermission: 1`** in `ProjectSettings.asset`. It was on Auto, where Unity infers
  the Android `INTERNET` permission from the code it can see. With IL2CPP and managed stripping that
  inference can miss Photon's sockets, producing an APK that simply never connects and says nothing
  about why.

`RunInBackground` is already 1 in both the Photon settings and the player settings, so alt-tabbing on
desktop does not drop the connection mid-match.

---

## 1. Three new components on `GameSystems`

`GameSystems` already carries `TutorialCoach`, `AudioManager`, `BoardFlipper` and `PassDeviceModal`.
All three of these go on the same object, for the reason the hot-seat doc gives: **it is always
active, so `Awake` always runs.** A modal driver on a panel that starts inactive never initialises
and silently no-ops.

### `PhotonMatchTransport`

No slots. It is the only class in the project that references Photon, and it holds no game state.

### `GameModals`

Drives all five popups. Slots are grouped by modal in the Inspector; see §3 for what to build.

| Group | Slot | Wire to |
|---|---|---|
| Motion | Fade In Seconds | `0.18` |
| Motion | Fade Out Seconds | `0.14` |
| Motion | Pop Scale | `0.92` |
| Motion | Spinner Degrees Per Second | `-180` |

The three motion values match `PassDeviceModal` and `TutorialCoach` exactly. Change them together or
the popups stop feeling like one family.

### `OnlineFlowController`

| Slot | Wire to | Notes |
|---|---|---|
| Transport | the `PhotonMatchTransport` on `GameSystems` | auto-found if empty |
| Menu Manager | the `MenuManager` | auto-found if empty |
| Game Controller | the `GameController` | auto-found if empty |
| Online Panel | `OnlinePanel` | new — see §2 |
| Lobby Panel | `LobbyPanel` | optional; leave empty to go straight into the game |
| Lobby Body Text | `LobbyPanel/LobbyBody` | |
| Lobby Seconds | `1.6` | |

---

## 2. New slots on `MenuManager`

| Slot | Wire to |
|---|---|
| Online Flow | the `OnlineFlowController` on `GameSystems` (auto-found if empty) |
| Tutorial Start Button | `…/Content/Tutorial/Start` — hidden while the rules are open from pause |

There is no `Play Online Selection Highlight`. That field existed only while online was a card on
the mode panel; both the field and the highlight object are gone. See §5.

---

## 3. Panels and modals

All of these were built by **deep-cloning `Canvas/TutorialTipPanel`** with fileID remapping, so they
inherit its `CanvasGroup`, backdrop, card, fonts and colours exactly rather than approximating them.
Buttons were cloned from `TipGotItButton` for the same reason. New fileIDs live in the `74xxxxxx`
block (`7100000xx`, `7200000xx` and `7300000xx` were already taken).

Every modal's four structural slots follow the same pattern, so they are listed once:

| Slot | Wire to |
|---|---|
| Panel Root | the duplicated panel |
| Backdrop | `<panel>/TipBackdrop` |
| Card | `<panel>/TipCard` |
| Canvas Group | the `CanvasGroup` on the panel |

### `OnlinePanel` (a normal panel, not a modal)

Reached from the welcome screen's PLAY ONLINE button (see §5), not from the mode panel. Cloned from
`ModePanel`, with its two highlights and its CONTINUE button removed — these cards act on tap rather
than selecting, because there is nothing to confirm.

> **Trap, hit and fixed on 2026-08-14.** Cloning `ModePanel` also cloned its **background sprite**,
> so `OnlinePanel` rendered `Mode.png` — the mode-selection artwork, "vs Computer" and "vs Human"
> labels included. Tapping PLAY ONLINE really did land on a screen that *was* the mode picker to
> look at, and it read as the button being wired wrong when the wiring was correct.
>
> Its Image now has no sprite and a flat charcoal fill (`0.153, 0.125, 0.090`), with a `PLAY ONLINE`
> title and `CREATE ROOM` / `JOIN ROOM` labels on the cards. **Replace this with an `Online.png` in
> the panel's Image slot** when the art exists; the labels can go at that point since the art will
> carry them, as every other panel does.

| Object | Button `onClick` |
|---|---|
| `OnlinePanel/CreateRoomCard` | `OnlineFlowController.CreateRoom` |
| `OnlinePanel/JoinRoomCard` | `OnlineFlowController.ShowJoinRoom` |
| `OnlinePanel/Back` | `OnlineFlowController.LeaveOnline` |

The cards themselves are the plain built-in rounded sprite with a scripted label on each. The
mode panel's look came from its background, not from them — see the trap note above.

### Create Room modal

| Slot | Wire to |
|---|---|
| Create Room Code Text | `CreateRoomPanel/TipCard/CodeText` — **large and centred**, this is the thing the player reads aloud |
| Copy Code Button | `CreateRoomPanel/TipCard/CopyButton` |
| Create Room Status Text | `CreateRoomPanel/TipCard/StatusText` |
| Create Room Spinner | `CreateRoomPanel/TipCard/Spinner` — optional; any image, it is just rotated |
| Create Room Cancel Button | `CreateRoomPanel/TipCard/CancelButton` |

Set the code label to a **monospace-ish TMP font asset and generous letter spacing**. Six characters
being read across a room is the whole point of this screen. The alphabet excludes `I`, `L`, `O`, `0`
and `1`, so you never have to disambiguate those — but `8`/`B` and `5`/`S` remain, so size matters.

### Join Room modal

| Slot | Wire to |
|---|---|
| Join Code Input | `JoinRoomPanel/TipCard/CodeInput` — a `TMP_InputField` |
| Join Confirm Button | `JoinRoomPanel/TipCard/JoinButton` |
| Join Cancel Button | `JoinRoomPanel/TipCard/CancelButton` |
| Join Status Text | `JoinRoomPanel/TipCard/StatusText` — optional |

> **This is the project's first `TMP_InputField`.** `ProfileManager` declares one but nothing is
> wired to it, so the username rename runs its fallback path and there was no existing field in the
> scene to copy — this one was assembled rather than cloned. Its structure is
> `CodeInput` (Image + `TMP_InputField`) → `TextArea` (`RectMask2D`) → `Placeholder` + `Text`, which
> is the shape TMP expects; the viewport is what clips a code longer than the box instead of letting
> it spill across the card.
>
> Character Limit is 6 and Character Validation is None — `RoomCode.Normalize` and `IsWellFormed`
> do the work in script, so the field never rewrites what the player typed. **Test the Android soft
> keyboard on device**: it has never run in this project.

The Join button is held non-interactable until six valid characters are entered.

### Room Not Found modal

| Slot | Wire to |
|---|---|
| Room Not Found Body Text | `RoomNotFoundPanel/TipCard/TipBody` |
| Room Not Found Try Again Button | `.../TryAgainButton` |
| Room Not Found Menu Button | `.../MenuButton` |

Body text is written by the script. Try Again reopens the Join modal; Menu leaves the room flow.

### Disconnect modal

| Slot | Wire to |
|---|---|
| Disconnected Body Text | `DisconnectPanel/TipCard/TipBody` |
| Disconnected Menu Button | `.../MenuButton` |
| Disconnected Play Computer Button | `.../PlayComputerButton` |
| Disconnected Play Human Button | `.../PlayHumanButton` |

The backdrop **dims** rather than blurs, matching every other popup in the game. A real blur needs a
URP renderer feature and a blit shader, which is a lot of machinery for one modal — that was the
agreed call, not an oversight. If you want the blur later, it is a change to the backdrop only and
nothing in the flow moves.

"Play vs Computer" starts at the last difficulty the player chose, so it is one tap rather than a
trip through a difficulty screen they did not ask for.

### Forfeit modal

| Slot | Wire to |
|---|---|
| Forfeit Body Text | `ForfeitPanel/TipCard/TipBody` |
| Forfeit Confirm Button | `.../ConfirmButton` |
| Forfeit Cancel Button | `.../CancelButton` |

**This replaces hot-seat's two-press pill.** The bottom pill no longer relabels itself to `CONFIRM?`
— one press now opens this. Local two-player and online ask the same question the same way.

> Button `onClick` handlers for every modal are attached **in script, not the Inspector**. They
> answer questions ("try again with which code?", "confirm what?") whose answers only exist when the
> modal is raised. Leave their `onClick` lists empty.

---

## 4. Button rewiring

| Button | `onClick` | Was |
|---|---|---|
| `WelcomePanel/PlayOnline` | `MenuManager.ShowOnline` | this object was `LearnToPlay` → `ShowTutorial` |
| `PausePanel/Tutorial` | `MenuManager.ShowTutorialFromPause` | new |
| `…/Content/Tutorial/Back` | `MenuManager.CloseTutorial` | `MenuManager.BackToWelcome` |

---

## 5. Where online is entered from — and why not the mode panel

**Online is reached from the welcome screen, not the mode panel.** It was briefly built as a third
card there and that was wrong twice over.

Practically: the mode panel selects then confirms, and both of its cards start a game the moment you
press CONTINUE. Online cannot — there is no game until a room exists and somebody else has joined
it. A third card would have behaved unlike its neighbours in a panel whose whole shape is "pick one,
then commit".

Physically: the art has room for two cards. Three meant narrowing all of them from 598px to 500px,
and since the cards carry their labels painted into the sprite rather than as child objects, that
squeezed the artwork rather than just the layout.

The welcome screen was reworked instead: **`LEARN TO PLAY` was replaced by `PLAY ONLINE`** in the
second pill, and Learn to Play moved into the pause menu as `TUTORIAL`. The four welcome buttons
already lined up with the four pills in the new `Main Menu.png`, so no object moved — the second one
was renamed and repointed.

### The pause menu's TUTORIAL button

`Pause.png` gained a third pill. **The redraw also moved the whole stack**, so every hit target on
that panel was left behind by roughly (53, 75) — and since the boxes were 160×30 against 383×70
pills, `Tutorial` ended up entirely *below* its pill and could not be tapped at all. The others
survived only because a low tap clipped their top edge.

All of them are now positioned and sized from the artwork itself rather than by eye. `PausePanel` is
full-screen with a stretched, non-aspect-preserving Image, so **PNG pixels map 1:1 onto the
1920×1080 canvas** — pill centres can be measured out of the file and used directly:

| Element | Position | Size |
|---|---|---|
| `Resume` | (−256.5, 171.8) | 383 × 104 |
| `Restart` | (−256.5, 40.2) | 383 × 70 |
| `Tutorial` | (−256.5, −84.8) | 383 × 70 |
| `MainMenu` | (−256.5, −219.8) | 383 × 70 |
| `X` | (467, 298.8) | 70 × 70 |
| `Music` | (264.5, 170.2) | 449 × 40 |
| `Sound` | (264.5, 55.2) | 449 × 40 |

Boxes are the full pill size, so the whole painted pill is tappable rather than a small patch in the
middle of it. **If any menu PNG is redrawn again, re-measure** — the buttons are invisible and
nothing about a misaligned one is visible until somebody taps it and nothing happens.

`Vibration` and `Instructions` were deliberately left alone: their knobs are drawn at runtime by
`VibrationSwitch` rather than painted into the art, so the artwork does not say where the control
belongs.

It is wired to `ShowTutorialFromPause`, **not** `ShowTutorial`, and that distinction matters:

- `ShowTutorial` calls `SetGameplayVisible(false)` and `ShowOnly(tutorialPanel)`, which takes the
  pause panel down along with the board. Reached from a game in progress, it ends that game.
- `ShowTutorialFromPause` leaves the gameplay root loaded and the clock frozen underneath, and
  remembers where it came from. `CloseTutorial` then returns to the pause menu rather than the
  welcome screen.

It also **hides the tutorial's START button** while open from pause — START goes to the mode panel
to begin a game, which offered to somebody already in one is a second way to lose it by accident.
`MenuManager.tutorialStartButton` is what that hides; `CloseTutorial` puts it back.

Note the pause button and the tutorial's own content object are both named `Tutorial`. Unity allows
it — they sit under different parents — but scripted lookups should disambiguate by parent.

---

## 6. Palette

Sampled from `SampleScene.unity` so the new panels match rather than approximate. Raw scene values,
with approximate sRGB hex:

| Role | Scene value | ≈ hex |
|---|---|---|
| Gold (primary accent) | `0.945, 0.745, 0.439` | `#F1BE70` |
| Gold (deeper / borders) | `0.820, 0.631, 0.345` | `#D1A158` |
| Coral (destructive, alerts) | `0.882, 0.451, 0.310` | `#E1734F` |
| Cream (body text) | `0.99, 0.94, 0.83` | `#FCF0D4` |
| Charcoal (card fill) | `0.160, 0.130, 0.100` | `#29211A` |
| Charcoal (panel fill) | `0.153, 0.125, 0.090` | `#272017` |
| Near-black (backdrop) | `0.071, 0.059, 0.055` | `#120F0E` |

Coral is the right colour for the Forfeit confirm button and the Disconnect modal's heading. Gold is
the room code. Everything else follows the existing cards.

---

## 7. What still needs your eyes

The headless check cannot see layout. In rough order of how likely it is to need work:

1. **`OnlinePanel` needs real art.** It is flat charcoal with scripted labels — functional and
   clearly not the mode panel, but plainly a placeholder next to the other screens.
2. **The pause panel's right-hand column.** The two sliders were re-measured and moved; the
   `Vibration` and `Instructions` toggles were not, for the reason in §5. Check they still line up
   with their labels.
3. **Modal card heights.** Create Room and Disconnect are 540 tall; the rest are the template's 420.
   Body text boxes were sized to fit but not measured against the real strings.
4. **The room code label.** Set to font size 110 in a 720×130 box. Six characters should fit
   comfortably, but it is the one label where clipping would matter most.
5. **The join field.** `CodeInput` is 560×110 with a `TextArea` viewport inset 24×16 and a
   `RectMask2D`, text at 72 and placeholder at 48. Check the caret and the soft keyboard on device —
   this is the project's first `TMP_InputField` and none of it has run on Android.
6. **The spinner.** A cloned highlight image at 56×56, rotated by script. It is a plain rounded
   sprite, not a spinner graphic — it will read as a turning blob until you give it something with
   a visible axis.

## 8. Player names

Photon carries nicknames itself, so none of this needed a protocol message.
`PhotonMatchTransport.LocalPlayerName` reads `PhotonNetwork.NickName` (set from `ProfileManager` on
connect) and `OpponentName` reads it off `PlayerListOthers`. Both fall back rather than showing a
blank for a player who never chose a username.

| Where | Shows |
|---|---|
| `OnlinePanel/OnlineIdentity` | "Playing as `<name>`" — read from the profile, since this screen appears before any connection exists |
| `LobbyPanel` body | both names with **both seat numbers** — the seat is the only thing that tells a player which rows are theirs |
| In-game status | "`<name>`'s turn", "Waiting for `<name>` to move…" |
| Move feedback | the local player is always "You"; the opponent is named |

---

## 9. What is deliberately not built

Each of these is a decision with a reason, not a gap:

- **No rematch.** Replaying online needs a "shall we play again" message and a screen for it. Restart
  on the game-over panel returns to the menu in online mode rather than dealing a board nobody is
  playing on.
- **Reconnect, in three layers.** A drop no longer ends the match. `RoomOptions.PlayerTtl` and
  `EmptyRoomTtl` hold the room and the seat for five minutes, and the device carries a Photon
  `UserId` that survives a restart, so the server can tell a returning player from a new one.
  - *Connection dropped, app alive.* `PhotonMatchTransport.OnDisconnected` calls
    `ReconnectAndRejoin` itself. No modal and nothing to answer — the player is put back on their
    board, and `NetworkMatch`'s resume exchange brings them up to date.
  - *App killed.* Nothing is left to reconnect, so the room code and seat are written to
    `SavedMatch` while play is under way and the next launch offers "Game In Progress — REJOIN".
    Accepting calls `IMatchTransport.RejoinRoom`, which is `RejoinRoom` and not `JoinRoom`: the
    held seat still counts against `MaxPlayers`, so an ordinary join asks to be a third player.
  - *The returning player was the host.* They come back with no board, so authority moves rather
    than the position: a client receiving a resume request that says `hasBoard: false` promotes
    itself and answers with its own mirror, which is safe because every result it holds was
    replayed and checked against the host's board before it was adopted. This is what protocol v3
    added.
- **No online stats.** The whole profile is keyed by AI difficulty; filing online games under a
  difficulty nobody played would make those numbers mean nothing. Online needs its own axis — an
  opponent context rather than a difficulty — which is a save-data migration and belongs with the
  profile rework in Phase 4 of `multiplayer-checklist.md`.
- **No turn timer.** Nothing stops a player sitting on their turn forever except leaving, which the
  opponent sees as a disconnect.
