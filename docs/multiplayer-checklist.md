# Nsolo: AI-opponent → human-vs-human transition checklist

Updated 2026-08-08. Line references are to the tree at that date; re-check them before working an item.

Agreed build order: finish v1 → push → refactor → multiplayer via Photon (low-level room +
`RaiseEvent`, not Fusion/Quantum).

---

## Phase 0 — Finish and ship v1  ✅

- [x] Commit the v1 polish: `AudioManager`, `Haptics`, `PitCountLabels`, `TutorialCoach`,
      `VibrationSwitch`, menu art, `SampleScene.unity`
- [x] Push as the backup build — `NsoloV1Final`, commit `0e4e3d6`
- [ ] Refactor pass (`GameController.cs` carries turn flow, animation sequencing, undo, hints and
      stats reporting in one class — now ~1000 lines with hot-seat added)

## Phase 1 — Decouple the turn loop from "the opponent is the AI"

- [x] **Opponent abstraction.** `IPlayerAgent` (`RequestMove → Task<Move>`), with `AIPlayerAgent`
      wrapping the existing `AIAgent` without modifying it, and `LocalHumanAgent` resolving a
      `TaskCompletionSource` when a pit is tapped. Same contract for a computed move and a tapped
      one; a network agent implements the same thing.
- [x] **Formation phase.** Both players arrange their own side in hot-seat, with a handover
      between. `GenerateAIFormation` still runs for single-player only.
- [x] **Undo and hints.** Hot-seat never pushes to `boardHistory`, and the Undo button is switched
      off with the background art that paints its pill. The shared bottom pill becomes FORFEIT.
- [~] **Seat, not constant.** Hot-seat runs off seat numbers; the single-player path still uses
      `humanPlayer`/`aiPlayer` fields. Finishing this is part of the refactor, not hot-seat.
- [~] **State machine describes what, not who.** `HotSeatTurn`/`HotSeatHandover` added alongside
      `HumanTurn`/`AiThinking` rather than replacing them, to leave the AI path untouched.
- [~] **Starting player.** Hot-seat always opens with player 1. Single-player keeps its
      `LastLoser_Diff{n}` rule. Neither is right for an online room.
- [ ] **Board rotation unit test.** The `r → 3-r`, `c → 7-c` claim about `SowingPath` is still
      unproven. Hot-seat did *not* need it — it orbits the camera and leaves board coordinates
      alone — but the network boundary will.

## Phase 2 — Pass-and-play  ✅

- [x] Local two-player on one device: mode panel → hot-seat game, both players arrange, handover
      card and an eased 180° camera orbit between turns, forfeit in place of hints.

Worth keeping in mind for Phase 3: the flip works by **orbiting the camera, not rotating the
board**. Stones are instantiated under `PitStoneVisualizer`'s own root at absolute world positions
rather than as children of the pits, so rotating the board would leave every stone behind. A side
effect is that tap→cell mapping needs no remapping at all — input is a physics raycast against pit
colliders that never move.

## Phase 3 — Photon online

Code complete 2026-08-13; **UI not built** — see `docs/online-wiring.md` for what to assemble.

- [x] **Scope settled**: play-with-a-friend-by-code. No matchmaking, no ranking.
- [x] PUN 2.55 imported. Its asmdefs are auto-referenced, so `Assembly-CSharp` needs no asmdef.
      **The App ID is still empty** in `PhotonServerSettings.asset` — nothing connects until it is set.
- [x] Room create/join by 6-character code. Collision handling is "try to create, regenerate on
      `GameIdAlreadyExists`" rather than a lobby pre-check, which cannot race. Alphabet excludes
      `I L O 0 1`.
- [x] Seat assignment: creator is player 1, joiner is player 2, captured once so PUN's master-client
      migration cannot renumber a seat mid-game.
- [x] Move sync via `RaiseEvent`, one event code, JSON payloads.
- [x] **Host-authoritative**, not client-authoritative — the user changed this. The host runs the
      real `GameEngine`; the client sends a pit and waits. The client's own legality check is a
      responsiveness pre-filter only.
- [x] Simultaneous formation exchange: both arrange at once, host assembles the 32-pit board and
      draws for who opens.
- [x] Disconnect handling: plain-language modal, dimmed backdrop, offers menu or a local game.
- [ ] Rejoin / turn-timeout — deliberately out of scope this pass, see below.
- [ ] Rematch flow — deliberately out of scope this pass.

**No rotation at the network boundary was needed.** The Phase 2 note turned out to settle it: the
flip orbits the *camera*, stones sit at absolute world positions, and input is a raycast against pit
colliders that never move. So the joiner parks their camera on their own side once at match start
and both devices speak identical board coordinates for the whole match. The `r → 3-r, c → 7-c`
mapping is real (with a **+8 path-index offset**, which the earlier note omitted) but is not used.

Architecture, for the record — the layering exists so persistence can be added without touching the
rules:

```
PhotonMatchTransport   ← the only file that references Photon. Carries strings.
        ↕ IMatchTransport
NetworkMatch           ← host authority, protocol, BoardStateChanged event. No Photon types.
        ↕ MoveResult
GameController         ← PlayMoveResult(result) animates whatever it is handed.
        ↕
GameEngine             ← untouched. Never sees a network type.
```

`NetworkMatch.BoardStateChanged` fires after every authoritative change and is the intended hook for
save-after-each-move. Nothing downstream of it can affect the rules.

## Phase 4 — Identity and stats

Hot-seat results are deliberately **not** recorded, and that should stay true permanently: either
seat can be any physical person, so per-seat W/L describes a chair, not a player. At most a
`localGamesPlayed` counter.

The core problem is that the whole profile is keyed on **one axis: AI difficulty**.
`RecordGameResult(int difficulty, bool won, float elapsed)` takes difficulty as its primary key,
and win rate, per-difficulty records, achievements and the "loser starts next" rule all hang off
it. Online has no difficulty. The fix is replacing that axis with an *opponent context*, not
renaming fields.

Reviewed 2026-08-10 — what breaks, field by field:

- [ ] **`gamesPlayed` / `gamesWon`** — ambiguous once a second opponent type exists. A 70% rate
      mixing Easy AI and ranked humans describes nothing. Split by context; the page must say which.
- [ ] **`easyWins/Losses`, `mediumWins/Losses`, `hardWins/Losses`** — keep, but caption them
      "vs Computer". Online's equivalent axis is opponent *rating band*, not interchangeable.
- [ ] **`currentWinStreak` / `longestWinStreak`** — highest risk. Merged across AI and human games,
      the "10 Game Streak" achievement is farmable in ~4 minutes on Easy. Needs two streaks.
- [ ] **`shortestWinSeconds`** — safe today only because the AI never rage-quits. Online, the
      fastest win becomes a move-three disconnect, permanently. Count clean wins only.
- [ ] **Achievements** — `FirstWin` and `TenGameStreak` are both farmable on Easy. `HardModeMaster`
      is genuinely fine as an AI-only achievement and should stay one.
- [ ] **`username`** — local label today, no uniqueness or length floor. Becomes a public identity.
      Needs a stable immutable player id separate from the display name so renames don't orphan
      match history.
- [ ] **`profile.json` in `persistentDataPath`** — local, trivially editable, lost on reinstall.
      Fine for AI stats; anything feeding matchmaking or a ladder must be server-side.
- [ ] **`LastLoser_Diff{n}`** (PlayerPrefs, `GameController`) — "loser starts next" is stored
      per-difficulty and has no online analogue. The room decides who opens. Also flagged in Phase 1.

Target shape — one record type instanced per context, replacing the flat difficulty-keyed fields:

```csharp
[Serializable]
public class OpponentRecord
{
    public int played, won, lost, forfeited, disconnected;
    public float shortestCleanWinSeconds;
    public int currentStreak, longestStreak;
}
```

One each for Easy/Medium/Hard/Online. `RecordGameResult` takes a context instead of an int; the
profile page picks a record rather than reading eight fields. **Do this during the refactor, before
Photon lands** — it is a save-data migration, and migrating one profile shape is far easier than
two.

New stats that only mean something online: rating, average move time (game duration is already
tracked), forfeit/disconnect rates given *and* received, rematch count, and head-to-head records if
the scope lands on play-with-a-friend-by-code rather than public matchmaking.

---

### Rejected, and still rejected

- **Unity Netcode for GameObjects + Relay** — host-authoritative P2P; matches die when the host leaves
- **Raw Firebase** — no way to run the C# engine server-side, so the subtle relay/capture-restart
  rules get reimplemented in JS and drift
- **Photon Fusion / Quantum** — built for tick/rollback sync; would drag the scene into
  `NetworkBehaviour` plumbing for 33 ints changing every 15 seconds
