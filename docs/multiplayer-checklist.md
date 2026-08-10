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

- [ ] **Settle the scope question with the supervisor**: play-with-a-friend-by-code vs public
      matchmaking with ranking. Gates everything else.
- [ ] Check Photon's current SDK lineup (it shifts), add the package — not in `manifest.json` today
- [ ] Room join by code; seat assignment; both-ready handshake
- [ ] Move sync via `RaiseEvent`: send `(row, col)`, rotate at the boundary, apply locally
- [ ] Client-authoritative with local validation — both clients run the identical deterministic
      `GameEngine`, so each rejects illegal opponent moves itself
- [ ] Disconnect, rejoin, and turn-timeout behaviour
- [ ] Rematch flow

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
