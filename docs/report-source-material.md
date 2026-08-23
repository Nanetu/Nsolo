# Nsolo — technical source material for the final report

Written 2026-08-15, against commit `41dfeb6` on `NsoloV3-online`.

Not a chapter draft. This is the fact base the chapters draw on: what the system actually does, with
file and line references, plus the design decisions that need a defensible one-line justification at
the defence. Verify line numbers before quoting them — they move.

---

## 1. What the artefact is

A digital implementation of **Nsolo**, a four-row mancala-family board game, built in Unity for
Android. Three ways to play:

| Mode | Opponent | Added |
|---|---|---|
| Single-player | `AIAgent` at Easy / Medium / Hard | v1 |
| Hot-seat | Second human, same device | v2 (`ef9b47b`) |
| Online | Second human, room code, Photon PUN2 | v3 (`41dfeb6`) |

The **AI search is the assessed component**. Everything else is the vehicle that demonstrates it.

### Board representation

`Core/GameBoard.cs` — 4 rows × 8 columns = 32 pits, stored as a flat `int[32]`, row-major.
Player 1 owns rows 0 (outer) and 1 (inner); player 2 owns rows 2 (inner) and 3 (outer). Every pit
opens with 2 stones, so 32 stones per player, 64 total.

Zobrist hashing at `GameBoard.cs:70-84`, table seeded deterministically with `new System.Random(42)`
so hashes are reproducible between runs and between devices.

### Rules, as implemented

`Core/GameEngine.cs` is stateless — it takes a board and returns a new one, which is what makes it
safe to call from inside a search and from inside a network handler.

- **Legal move**: any pit on your own side holding **≥ 2 stones** (`GameEngine.cs:33-51`). A pit
  with one stone cannot be lifted.
- **Sowing**: counterclockwise, **within your own two rows only** — a 16-hole loop per player, never
  onto the opponent's side (`Core/SowingPath.cs`). This is the rule that most distinguishes Nsolo
  from the Kalah-style games that dominate the mancala AI literature.
- **Rule 1 — turn ends**: the last stone lands in a pit that was empty before it arrived
  (`GameEngine.cs:94-100`).
- **Rule 2 — capture**: the last stone lands in an occupied pit on your **inner** row, and both of
  the opponent's pits in that same column are occupied. Both are emptied, and the captured stones
  are resown **starting from after the pit originally lifted this turn** — the capture-restart rule
  (`GameEngine.cs:116-131`). The landing pit itself keeps its stones.
- **Rule 2 — relay**: same landing, but the capture condition fails. Lift the landing pit and keep
  sowing (`GameEngine.cs:132-139`).
- **Rule 3 — outer-row relay**: landing occupied on your **outer** row always relays, unconditionally
  (`GameEngine.cs:141-150`).
- **Terminal**: the player to move has no legal move; the opponent wins (`GameEngine.cs:203-214`).

Relay and capture chains loop until Rule 1 fires. A hard stop at 10 000 segments guards against a
non-terminating chain (`GameEngine.cs:11`, `:84-88`) — it logs an error rather than hanging the app.

**Report note.** The single-loop-per-player sowing path plus the capture-restart rule means published
mancala results are not directly comparable. That is worth stating explicitly rather than glossing:
it justifies building an evaluation function from scratch instead of adapting a documented one.

---

## 2. The AI search — `AI/AIAgent.cs`

Iterative-deepening minimax, alpha-beta pruning, transposition table, move ordering.

### Difficulty (`AIAgent.cs:46-66`)

| | Easy | Medium | Hard |
|---|---|---|---|
| `maxDepth` | 1 | 3 | 6 |
| `randomMoveProbability` | 0.9 | 0.45 | 0.0 |
| `timeBudgetMs` | 3000 | 3000 | 3000 |

Difficulty is **two independent dials** — depth and a random-move coin flip (`AIAgent.cs:82-89`).
Worth defending explicitly in the report: depth alone made Easy feel slow-but-still-sharp, whereas
noise makes it feel beatable without making it feel broken. Hard is pure search, no noise.

### Iterative deepening (`AIAgent.cs:100-155`)

Searches depth 1, 2, 3… and keeps the best move from the **last fully completed depth**
(`:147-148`). A depth interrupted by the time budget is discarded, since it may have examined only
a few root moves — except at depth 1, where a partial answer still beats no answer. Guarantees a
usable move at any interruption point and keeps the UI responsive.

The 3000 ms budget, not `maxDepth`, is what actually bounds thinking time at Hard.

### Transposition table (`AI/TranspositionTable.cs`)

`Dictionary<long, TranspositionEntry>`, capped at 100 000 entries. Eviction policy is "stop
inserting when full" (`TranspositionTable.cs:51-52`) — not LRU, not depth-preferred. Cleared at the
start of every `SearchBestMove` (`AIAgent.cs:106`), so it is shared **across depths within one
search** but never between moves.

Entries carry EXACT / LOWER / UPPER bound flags used for the standard alpha-beta window narrowing
(`AIAgent.cs:168-181`).

### Move ordering (`AIAgent.cs:279-328`)

Composite score, searched high to low:

| Signal | Weight |
|---|---|
| Previous-depth best move | +100 000 |
| Stones captured | ×1 000 |
| Extra sowing segments (relay chains) | ×120 |
| Landing-sequence length | ×5 |
| Stones in the source pit | ×1 |
| History-heuristic score from prior cutoffs | accumulated |

History scores accumulate `depth²` plus capture and relay bonuses on every beta cutoff
(`AIAgent.cs:310-323`).

Note that `CreateMoveOrderingInfo` calls `ApplyMoveWithResult` for **every move at every node**
(`AIAgent.cs:296`) — ordering is not free here, it costs a full move simulation per candidate. That
is a measurable tradeoff and a good thing to quantify in the evaluation chapter.

### Evaluation function (`AI/EvaluationFunction.cs`)

Weighted sum of four heuristics:

| Component | Weight | What it measures |
|---|---|---|
| Stone difference | 0.55 | Own stones − opponent stones |
| Mobility | 0.20 | Count of own pits with ≥ 2 stones |
| Capture threat | 0.15 | Opponent stones sitting in capturable columns, ×0.1 each |
| Relay potential | 0.10 | Own occupied pits, ×0.05 each — a board-density proxy |

Captured stones land in the capturing player's own pits, so stone difference already folds in
capture advantage — no separate "score" term is needed (`EvaluationFunction.cs:30-36`). Weights are
hand-tuned and settable at runtime, which is what would make a weight-tuning experiment cheap.

### Hint system (`AIAgent.cs:330-345`)

Reuses the identical search at depth cap 6 but a **700 ms** budget, and runs independently of the
selected difficulty — hints are strong even on Easy. Defensible in one line: a hint is a teaching
aid, not an opponent, so it should not be handicapped.

---

## 3. ✔ Defects in the assessed component — both now fixed

**Fixed on 2026-08-15, before any benchmarking.** Kept in full below because they are the strongest
material available for a "problems encountered" section: both were silent, both were found by
reading rather than by a failure, and neither would have shown up in play. What follows describes
the code as it was; the fix applied is recorded at the end of each.

Both were in the search. Both are the kind of thing an examiner may probe directly.

### 3.1 The search never maximises below the root

`AIAgent.cs:224` and `AIAgent.cs:245` both pass this as the child's `isMaximising` argument:

```csharp
board.CurrentPlayer != currentPlayer
```

`currentPlayer` is assigned from `board.CurrentPlayer` at `AIAgent.cs:192`, and `board` is never
reassigned in between. `GameEngine.ApplyMoveWithResult` clones before mutating
(`GameEngine.cs:64`) and only ever sets `newBoard.CurrentPlayer` (`GameEngine.cs:159`), so the input
board is untouched. **The expression is therefore always `false`.**

Consequence: the root calls its children with `isMaximising: false`, which is correct — after the
AI moves it is the opponent's turn. But every node below that also receives `false`. Nodes where it
is the AI's turn again are minimised instead of maximised. At depth ≥ 2 the search models an
opponent who plays against the AI *and* an AI that plays against itself.

This affects Medium (depth 3) and Hard (depth 6). Easy searches depth 1 and is unaffected. It does
not crash, and it still returns a legal, superficially plausible move — which is exactly why it has
survived. The likely intended expression is `moveResult.Board.CurrentPlayer == aiPlayer`.

**Fix applied:** both recursive calls now pass `moveResult.Board.CurrentPlayer == aiPlayer` — read
off the *child* board rather than the parent's, which is what makes it vary. The root still passes
`false` explicitly, which was always correct. `Minimax`'s doc comment now states the invariant the
argument has to satisfy, since nothing in the signature enforces it.

**Do not benchmark against numbers taken before this.** Any strength figures measured on the old
search describe the broken one, and every table in the chapter has to come from the fixed build.

### 3.2 Zobrist hash omits side-to-move

`GameBoard.HashCode()` (`GameBoard.cs:70-84`) folds in only the 32 pit counts. `CurrentPlayer` is
not part of the hash, so two nodes with identical stone layouts but opposite players to move share a
transposition entry and can read back each other's scores.

Standard fix is one extra XOR against a side-to-move key. Cheap, and it removes a class of search
error that is hard to argue away in a viva.

Note these interact: 3.1 makes 3.2 harder to observe, because a search that minimises everywhere is
less sensitive to which side a stored score belonged to.

---

## 4. Architecture — the layering argument

The strongest structural claim the report can make is that **the rules engine never learns that
networking exists**.

```
PhotonMatchTransport   ← the only file that references Photon. Carries strings.
        ↕ IMatchTransport
NetworkMatch           ← host authority, protocol, BoardStateChanged. No Photon types.
        ↕ MoveResult
GameController         ← PlayMoveResult(result) animates whatever it is handed.
        ↕
GameEngine             ← untouched by v2 and v3. Never sees a network type.
```

Two seams do the work:

**`IPlayerAgent`** (`Players/IPlayerAgent.cs`) — `Task<Move> RequestMove(board, player, token)`.
The AI resolves its task when minimax returns; a local human resolves it when a pit is tapped; a
network opponent resolves it when the move arrives. The turn manager cannot tell them apart.
`AIPlayerAgent` (`Players/AIPlayerAgent.cs`) is a deliberate 39-line wrapper that pushes
`AIAgent.SelectMove` onto the thread pool and changes nothing about the search — the assessed code
stayed unrisked through two feature milestones. That is a defensible engineering decision worth
stating as one.

**`IMatchTransport`** (`Net/IMatchTransport.cs`) — Photon appears in exactly one file
(`Net/PhotonMatchTransport.cs`). Swapping backends means writing one class.

---

## 5. Online multiplayer — `Net/`

Scope: **play with a friend by room code.** No matchmaking, no ranking. Deliberate, and defensible
in one line: matchmaking needs a population the project will not have, and a ladder needs
server-side identity the project deliberately does not build.

### Host-authoritative

The room creator runs the real `GameEngine`; the joiner sends a pit and waits. The joiner's own
legality check is a **responsiveness pre-filter only** and is not trusted (`NetworkMatch.cs:364-371`).

The detail worth writing up: the host **broadcasts to itself** rather than shortcutting to its own
screen (`NetworkMatch.cs:340-347`). Both devices, host included, animate from the same authoritative
packet, so there is one code path from "a move happened" to "the stones moved" instead of two that
must be kept in agreement by hand.

### The packet, and why it is small

A long relay chain can produce a hundred-plus landing positions. The packet carries only the move
and the resulting 32-int board; the receiver rebuilds the full `MoveResult` by **replaying the move
locally**, which the deterministic engine guarantees will match, and the board in the packet is the
proof that it did (`NetworkMatch.cs:380-425`). A mismatch adopts the host's board and raises
`Desynced` rather than silently patching (`NetworkMatch.cs:410-418`).

### Other decisions with a reason attached

- **Seats captured once** at match start (`NetworkMatch.cs:140-147`). PUN migrates master-client on
  a drop; a seat number changing mid-game would be worse than a stale one.
- **Room codes**: 6 characters, alphabet excludes `I L O 0 1`. Collision handling is try-to-create
  and regenerate on `GameIdAlreadyExists` (`Net/RoomCode.cs`) — a lobby pre-check would race.
- **Coin flip for who opens** (`NetworkMatch.cs:296-300`), not "creator always opens". Nsolo's
  opening matters; a permanent first-move advantage to whoever tapped Create Room is a competitive
  tilt, not a cosmetic one.
- **Forfeit is broadcast, not routed through the host** (`NetworkMatch.cs:210-221`). Nothing to
  validate — a player can only concede on their own behalf — so routing it would add a hop and a
  failure mode to an already unforgeable message.
- **`awaitingOwnBroadcast`** (`NetworkMatch.cs:33-40`) closes the window where a second request
  could be played twice before the first result returns.
- **Protocol version check** on every message (`NetworkMatch.cs:227-232`); unknown actions are
  dropped, not treated as errors, so a later build can add messages without breaking this one.
- **No board rotation at the network boundary.** The camera orbits, stones sit at absolute world
  positions, input is a raycast against colliders that never move — so both devices use identical
  board coordinates all match. The `r → 3-r, c → 7-c` mapping is real (with a +8 path-index offset)
  but is not needed.
- **`FixedRegion: za`** — Photon rooms are region-scoped. Left empty, two clients can ping into
  different regions and a valid code returns "room not found". Good concrete example for a
  "problems encountered" section.

### Deliberately out of scope

Rejoin, turn timeout, and rematch. Also **hot-seat results are not recorded**, permanently: either
seat can be any physical person, so per-seat W/L describes a chair, not a player.

### Rejected alternatives, with reasons

- **Unity Netcode for GameObjects + Relay** — host-authoritative P2P; matches die when the host leaves.
- **Raw Firebase** — no way to run the C# engine server-side, so the relay and capture-restart rules
  get reimplemented in JS and drift.
- **Photon Fusion / Quantum** — built for tick/rollback sync; would drag the scene into
  `NetworkBehaviour` plumbing for 33 ints that change every 15 seconds.
- **A custom authoritative server** — removed because "who pays for and maintains it" was the single
  most likely thing to kill the project.

---

## 6. Known limitations to own in the write-up

Better stated by the author than found by an examiner.

- **§3.1 and §3.2** above, unless fixed first.
- **No automated tests.** No unit tests on the engine, no regression suite. The board-rotation claim
  in `docs/multiplayer-checklist.md` is still explicitly unproven.
- **Terminal scores ignore depth** (`AIAgent.cs:196`, `:204`) — `float.MaxValue` for any win, so the
  AI does not prefer a faster win or a slower loss.
- **Transposition eviction is "stop inserting"**, so a long search fills the table with early,
  shallow entries and stops caching the deeper ones that matter more.
- **`GameController.cs` is 1 659 lines** carrying turn flow, animation sequencing, undo, hints and
  stats reporting. The refactor is still open at Phase 0 of the checklist.
- **Profile stats are keyed on one axis, AI difficulty** — no online analogue.
  `docs/multiplayer-checklist.md` §Phase 4 has the full field-by-field breakdown and the proposed
  `OpponentRecord` shape. Not yet done.
- **`profile.json` in `persistentDataPath`** — local, trivially editable, lost on reinstall.
- **Photon free tier caps 20 concurrent users**, which bounds any scale claim.

---

## 7. The evaluation-chapter gap

**No measurements exist.** Nothing in the repo records node counts, search timings, effective
branching factor, pruning effectiveness, win rates, or playtest results.

This is the largest single hole in the thesis, and it is fixable in software — the engine is
stateless, deterministic and Unity-independent enough to drive from a headless harness. Candidates,
roughly in order of value per unit effort:

1. **Search performance vs depth** — nodes visited, wall-clock time, and how often the 3 000 ms
   budget truncates a depth. Directly evidences the iterative-deepening design.
2. **Alpha-beta and ordering effectiveness** — nodes with pruning vs without, and with the ordering
   heuristics disabled one at a time. This is the classic table for a search chapter and the
   ordering code is already instrumented enough to support it.
3. **Transposition hit rate** and the effect of the 100 000-entry cap.
4. **Playing strength** — round-robin Easy/Medium/Hard self-play over N games, win rates and average
   game length. Also the experiment that would *demonstrate* §3.1, before and after.
5. **Evaluation weight sensitivity** — the four weights are already runtime-settable.
6. **Human playtesting** — even 5–10 participants with a short questionnaire supports the
   difficulty-calibration claim, which is otherwise pure assertion.

Items 1–5 need no participants and no ethics approval.

---

## 8. Source documents in this repo

- `docs/multiplayer-checklist.md` — phased transition plan, status of record, Phase 4 stats analysis,
  rejected alternatives.
- `docs/online-wiring.md` — the online UI wiring contract, plus the Photon setup notes.
- `docs/hotseat-wiring.md` — the same for hot-seat.
- Git history: `9856598` v1 deployed → `0e4e3d6` v1 final → `ef9b47b` v2 hot-seat → `41dfeb6` v3
  online. Useful for a development-methodology section that needs an actual timeline.
