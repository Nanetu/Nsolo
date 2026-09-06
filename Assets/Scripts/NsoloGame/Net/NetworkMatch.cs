using System;
using System.Collections.Generic;
using NsoloGame.Core;
using UnityEngine;

namespace NsoloGame.Net
{
    /// <summary>
    /// One online game, from the rules' point of view.
    ///
    /// This is where "host-authoritative" actually means something. The host runs the real
    /// <see cref="GameEngine"/> against its own board and broadcasts what happened; the client sends
    /// requests and renders answers. Neither side's animation is driven by a tap — both are driven
    /// by an authoritative result, including the host's own, which is why the host broadcasts to
    /// itself rather than shortcutting straight to its screen. One code path, both devices.
    ///
    /// It holds no Photon types. Everything network-shaped is behind <see cref="IMatchTransport"/>,
    /// and everything rules-shaped is in <see cref="GameEngine"/>, which this class calls and never
    /// modifies. The seam matters for what comes next: <see cref="BoardStateChanged"/> fires on
    /// every authoritative state change, so a persistence layer can save after each move by
    /// subscribing here — without a database call ever appearing inside the move or capture logic.
    /// </summary>
    public class NetworkMatch
    {
        private readonly IMatchTransport transport;
        private readonly GameEngine engine;

        /// <summary>Formations collected during setup, indexed by player. Host-side only.</summary>
        private readonly int[][] formations = new int[3][];

        private bool started;

        /// <summary>
        /// A begin packet has been seen. Either player may send one, so two can be in flight at
        /// once when both tap at the same moment; without this the second would send everyone to
        /// the arrangement screen a second time, on top of the one they are already using.
        /// </summary>
        private bool begun;

        /// <summary>
        /// Host-side: a result has been broadcast and has not come back yet. Until it does,
        /// <see cref="CurrentPlayer"/> still names the player who just moved, so without this a
        /// second request arriving inside that window would pass the turn check and be played twice.
        /// The local agents already only ever offer one move at a time — this is here because the
        /// point of a host is not to depend on the other client behaving.
        /// </summary>
        private bool awaitingOwnBroadcast;

        /// <summary>
        /// When <see cref="awaitingOwnBroadcast"/> was raised, so it cannot latch forever.
        ///
        /// The flag is cleared by the broadcast coming back. Photon sends reliably, so it always
        /// should — but "always" here means "unless the connection hiccups at the wrong moment",
        /// and the failure mode if it does is the worst kind: every later move is refused as "one
        /// still in flight" and the match is frozen with no error on screen and no way out but
        /// quitting. Treating a result that has not returned within
        /// <see cref="BroadcastTimeoutSeconds"/> as lost turns a dead match into a skipped beat.
        /// </summary>
        private float awaitingSince;

        /// <summary>
        /// How long to wait for our own broadcast before assuming it is not coming. Far longer than
        /// a round trip, because clearing this early would let a genuinely in-flight move be played
        /// twice — the exact thing the flag exists to prevent.
        /// </summary>
        private const float BroadcastTimeoutSeconds = 5f;

        private readonly Action<string> messageHandler;
        private readonly Action<MatchEndReason> matchEndedHandler;
        private readonly Action<MatchInterruption> matchInterruptedHandler;
        private readonly Action matchResumedHandler;

        public NetworkMatch(IMatchTransport transport, GameEngine engine)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

            // Held as fields so Detach can take them off again. The transport outlives any one
            // match — it sits on GameSystems — so a match that subscribed and went away without
            // unsubscribing would keep handling messages for a game that no longer exists.
            messageHandler = HandleMessage;
            matchEndedHandler = reason => MatchEnded?.Invoke(reason);
            matchInterruptedHandler = interruption => MatchInterrupted?.Invoke(interruption);
            matchResumedHandler = HandleTransportResumed;

            transport.MessageReceived += messageHandler;
            transport.MatchEnded += matchEndedHandler;
            transport.MatchInterrupted += matchInterruptedHandler;
            transport.MatchResumed += matchResumedHandler;
        }

        /// <summary>
        /// Stops this match listening. Call when leaving an online game, before starting another.
        /// </summary>
        public void Detach()
        {
            transport.MessageReceived -= messageHandler;
            transport.MatchEnded -= matchEndedHandler;
            transport.MatchInterrupted -= matchInterruptedHandler;
            transport.MatchResumed -= matchResumedHandler;
        }

        /// <summary>
        /// Which seat this device plays. The room's creator is player 1 and the joiner is player 2,
        /// captured once when the match starts rather than read from the transport each time — PUN
        /// can hand the host role to the remaining player when someone drops, and a seat number that
        /// changed underneath a game in progress would be far worse than a stale one.
        /// </summary>
        public int LocalPlayer { get; private set; }

        public int RemotePlayer => LocalPlayer == 1 ? 2 : 1;

        /// <summary>Whether this device is the authority that runs the real rules.</summary>
        public bool IsHost { get; private set; }

        /// <summary>
        /// Whether these two have left the lobby. True from the moment they go off to arrange their
        /// stones, which is the point after which the room's screens must not be shown again: an
        /// opponent rejoining makes the transport report a full room a second time, and without
        /// this the device that stayed would be pulled out of its game and back into the lobby.
        /// </summary>
        public bool InProgress => begun;

        /// <summary>This player's display name.</summary>
        public string LocalPlayerName => transport.LocalPlayerName;

        /// <summary>
        /// The opponent's display name, falling back to "Opponent" so callers can use it directly
        /// in text without each one repeating the null check.
        /// </summary>
        public string OpponentName => string.IsNullOrWhiteSpace(transport.OpponentName)
            ? "Opponent"
            : transport.OpponentName;

        /// <summary>The authoritative board. Only ever replaced by an authoritative message.</summary>
        public GameBoard Board { get; private set; }

        /// <summary>Whose turn it is. Meaningful once <see cref="MatchStarted"/> has fired.</summary>
        public int CurrentPlayer { get; private set; }

        /// <summary>
        /// A move resolved authoritatively. Carries the full <see cref="MoveResult"/> — landing
        /// sequence, sowing segments, captures — so the existing animation path can play it exactly
        /// as it plays a local one.
        /// </summary>
        public event Action<MoveResult> MoveApplied;

        /// <summary>
        /// One of the two players has pressed START GAME. Both devices leave the lobby and go to
        /// the board to arrange their stones; no board state exists yet. Fires once per match, so a
        /// second begin packet — both players tapping at once — is not a second trip to the board.
        /// </summary>
        public event Action MatchBegun;

        /// <summary>Both formations are in and play can begin. Carries the board and who opens.</summary>
        public event Action<GameBoard, int> MatchStarted;

        /// <summary>Somebody conceded. Carries the player who gave up, not the winner.</summary>
        public event Action<int> Forfeited;

        /// <summary>
        /// The authoritative board changed, for anything that wants to observe state rather than
        /// events. This is the intended hook for saving a match to a database later: it fires after
        /// every authoritative change, and nothing downstream of it can affect the rules.
        /// </summary>
        public event Action<GameBoard> BoardStateChanged;

        /// <summary>The match cannot continue. Drives the disconnect modal.</summary>
        public event Action<MatchEndReason> MatchEnded;

        /// <summary>
        /// A connection dropped and the match is being held open. Play stops; nothing is torn down.
        /// Passed straight through from the transport — this layer has nothing to add to it.
        /// </summary>
        public event Action<MatchInterruption> MatchInterrupted;

        /// <summary>
        /// The connection is whole again, on both devices. Play can continue and the waiting
        /// message can come down.
        ///
        /// Says nothing about the position, which is the next question and not always the same
        /// answer: the host never lost its board and has nothing to restore, while a client may be
        /// several moves behind and finds out through <see cref="MatchResynced"/> a round trip
        /// later. Splitting the two keeps "you are connected" from having to wait on "and here is
        /// where we got to", which would leave the host frozen behind its own broadcast.
        /// </summary>
        public event Action MatchResumed;

        /// <summary>
        /// The authoritative position, after a reconnect left this device unsure of it. Carries the
        /// board and whose turn it is, exactly as <see cref="MatchStarted"/> does — but this is a
        /// game being picked up rather than begun, and the two must not be confused: one restores a
        /// position, the other opens a new one.
        ///
        /// Fires on the client only, and only when there was a game in progress to restore. The
        /// host is the source of this and has nothing to learn from it.
        /// </summary>
        public event Action<GameBoard, int> MatchResynced;

        /// <summary>
        /// The two boards disagreed. This should be unreachable — the engine is deterministic and
        /// both sides run the identical build — so it is reported rather than quietly patched over.
        /// The authoritative board is adopted regardless.
        /// </summary>
        public event Action Desynced;

        // ── Setup ─────────────────────────────────────────────────────────

        /// <summary>
        /// Fixes the seat assignment. Called when the transport reports both players present, which
        /// can happen more than once — the caller re-asserts it in case the first call came before
        /// the host role was meaningful.
        ///
        /// Ignored once the match has left the lobby. The seat is read from the transport's idea of
        /// who the host is, and PUN moves that role to whoever is left when somebody drops — so a
        /// player returning to a game in progress makes this fire again on the device that stayed,
        /// with an answer that is now wrong. It would swap that player's side of the board, throw
        /// away the formations, and hand authority back to a device that may not have a board. The
        /// seat is settled when the players sit down; nothing after that gets to move it.
        /// </summary>
        public void AssignSeats(bool isHost)
        {
            if (begun)
            {
                Debug.Log($"NetworkMatch: seats already settled (player {LocalPlayer}, host={IsHost}); ignoring a re-assignment.");
                return;
            }

            IsHost = isHost;
            LocalPlayer = isHost ? 1 : 2;
            started = false;
            begun = false;
            formations[1] = null;
            formations[2] = null;
        }

        /// <summary>
        /// Seats a match that is being rejoined rather than started: this device is coming back to a
        /// game already in progress, from a saved record of which seat it was in.
        ///
        /// Seat and authority are set separately here, which is the whole point. Everywhere else the
        /// two travel together because the room's creator is both player 1 and the rules-runner, but
        /// a device that has just been relaunched holds no board — so whatever it used to be, it
        /// cannot be the authority now. It comes back as a player and asks for the position; the
        /// device that still has one answers, taking over as authority if it was not already
        /// (see <see cref="HandleResumeRequest"/>).
        ///
        /// Marked as begun so the <see cref="AssignSeats"/> call that follows the transport
        /// reporting a full room cannot undo any of this.
        /// </summary>
        public void RestoreSeat(int seat)
        {
            if (seat is not (1 or 2))
            {
                Debug.LogError($"NetworkMatch: cannot restore seat {seat}; it must be 1 or 2.");
                return;
            }

            LocalPlayer = seat;
            IsHost = false;
            started = false;
            begun = true;
            formations[1] = null;
            formations[2] = null;
        }

        /// <summary>
        /// Asks whoever holds the position to send it. Used by a device that has rejoined a match it
        /// has no memory of, where the transport's own resume path never runs because from its point
        /// of view this is an ordinary join.
        /// </summary>
        public void RequestResume()
        {
            transport.SendToHost(NetProtocol.ToJson(new ResumeRequestMessage(LocalPlayer, hasBoard: false)));
        }

        /// <summary>
        /// Leaves the lobby and sends both players to arrange their stones. Either player may call
        /// it.
        ///
        /// This is not the start of play, which is the reason it is not the host's alone: it opens
        /// the arrangement phase, where each side lays out its own half independently and at its own
        /// pace, and the first move cannot happen until both formations have arrived
        /// (<see cref="StartMatch"/>). Nobody is dropped into a live game by the other pressing it,
        /// so restricting it bought no protection and cost the joiner a room they could not get out
        /// of the lobby of when the host put their phone down.
        ///
        /// Authority is unaffected. The host still runs every rule; this packet carries no state.
        /// </summary>
        public void RequestBegin()
        {
            if (begun) return;

            transport.Broadcast(NetProtocol.ToJson(new BeginMessage()));
        }

        /// <summary>
        /// Submits this player's opening formation: the 16 pits on their own side, in row-major
        /// order. Both players arrange at the same time and neither waits for the other to finish,
        /// so this can arrive in either order.
        /// </summary>
        public void SubmitLocalFormation(int[] cells)
        {
            if (cells == null || cells.Length != 16)
            {
                Debug.LogError($"NetworkMatch: a formation must be 16 pits, got {cells?.Length ?? 0}.");
                return;
            }

            if (IsHost)
            {
                // No round trip for our own: the host is the one assembling them.
                StoreFormation(LocalPlayer, cells);
                return;
            }

            transport.SendToHost(NetProtocol.ToJson(new FormationMessage(LocalPlayer, cells)));
        }

        // ── Local actions ─────────────────────────────────────────────────

        /// <summary>
        /// Asks for a move to be played. Called when the local player taps a pit.
        ///
        /// The host validates and executes its own request through exactly the same path it uses for
        /// the client's, so there is one implementation of the rules and one of the validation. The
        /// client only sends — it computes no captures and changes no state, and its board does not
        /// move until the host's answer arrives.
        /// </summary>
        public void RequestMove(Move move)
        {
            if (move == null || Board == null) return;

            if (IsHost)
            {
                ExecuteAuthoritativeMove(LocalPlayer, move.Row, move.Col);
                return;
            }

            transport.SendToHost(NetProtocol.ToJson(new MoveRequestMessage(LocalPlayer, move.Row, move.Col)));
        }

        /// <summary>
        /// Concedes.
        ///
        /// Unlike a move, this is broadcast directly rather than routed through the host for
        /// validation. There is nothing to validate: a player can only ever concede on their own
        /// behalf, and no board state is computed from it. Sending it through the host would add a
        /// hop and a failure mode to a message that is already unforgeable.
        /// </summary>
        public void RequestForfeit()
        {
            transport.Broadcast(NetProtocol.ToJson(new ForfeitMessage(LocalPlayer)));
        }

        // ── Message handling ──────────────────────────────────────────────

        private void HandleMessage(string json)
        {
            int version = NetProtocol.PeekVersion(json);
            if (version != NetProtocol.Version)
            {
                Debug.LogError($"NetworkMatch: ignoring a message built for protocol v{version}; this build speaks v{NetProtocol.Version}.");
                return;
            }

            switch (NetProtocol.PeekAction(json))
            {
                case NetProtocol.ActionBegin:
                    if (begun) break;

                    begun = true;
                    MatchBegun?.Invoke();
                    break;
                case NetProtocol.ActionFormation:
                    HandleFormation(json);
                    break;
                case NetProtocol.ActionStart:
                    HandleStart(json);
                    break;
                case NetProtocol.ActionMove:
                    HandleMoveRequest(json);
                    break;
                case NetProtocol.ActionResult:
                    HandleMoveResult(json);
                    break;
                case NetProtocol.ActionForfeit:
                    HandleForfeit(json);
                    break;
                case NetProtocol.ActionResumeRequest:
                    HandleResumeRequest(json);
                    break;
                case NetProtocol.ActionResumeState:
                    HandleResumeState(json);
                    break;
                default:
                    // Unknown actions are dropped rather than treated as errors, so a later build
                    // can add messages without this one falling over.
                    break;
            }
        }

        private void HandleFormation(string json)
        {
            if (!IsHost) return;

            FormationMessage message = NetProtocol.FromJson<FormationMessage>(json);
            if (message?.cells == null || message.cells.Length != 16) return;

            StoreFormation(message.player, message.cells);
        }

        private void StoreFormation(int player, int[] cells)
        {
            if (player is not (1 or 2)) return;

            formations[player] = cells;

            if (formations[1] == null || formations[2] == null) return;
            if (started) return;

            StartMatch();
        }

        /// <summary>
        /// Assembles both halves into one board and tells everyone to begin. Host only.
        /// </summary>
        private void StartMatch()
        {
            started = true;

            var board = new GameBoard();
            WriteHalf(board, 1, formations[1]);
            WriteHalf(board, 2, formations[2]);

            // Deliberately not "the creator always opens". Nsolo's opening matters, and handing a
            // permanent first-move advantage to whoever happened to tap Create Room would be a real
            // competitive tilt rather than a cosmetic one. The choice is the host's to make and
            // travels explicitly in the packet, so neither side has to infer it.
            int firstPlayer = UnityEngine.Random.value < 0.5f ? 1 : 2;

            board.CurrentPlayer = firstPlayer;
            transport.Broadcast(NetProtocol.ToJson(new StartMessage(board.Board, firstPlayer)));
        }

        /// <summary>Writes one player's 16 pits into their own two rows.</summary>
        private static void WriteHalf(GameBoard board, int player, int[] cells)
        {
            int firstRow = player == 1 ? 0 : 2;

            for (int i = 0; i < 16; i++)
                board.Set(firstRow + i / GameBoard.Cols, i % GameBoard.Cols, cells[i]);
        }

        private void HandleStart(string json)
        {
            StartMessage message = NetProtocol.FromJson<StartMessage>(json);
            if (message?.board == null || message.board.Length != GameBoard.HoleCount) return;

            started = true;
            Board = BoardFrom(message.board, message.firstPlayer);
            CurrentPlayer = message.firstPlayer;

            BoardStateChanged?.Invoke(Board);
            MatchStarted?.Invoke(Board, message.firstPlayer);
        }

        /// <summary>
        /// The host receiving a client's request. Validates it against the real board and, if it
        /// stands, executes and broadcasts.
        /// </summary>
        private void HandleMoveRequest(string json)
        {
            if (!IsHost) return;

            MoveRequestMessage message = NetProtocol.FromJson<MoveRequestMessage>(json);
            if (message == null) return;

            ExecuteAuthoritativeMove(message.player, message.row, message.col);
        }

        /// <summary>
        /// The only place a move is ever really played. Host only.
        ///
        /// Note what it does not do: it does not touch <see cref="Board"/>. The host applies the
        /// move the same way the client does — by receiving the broadcast it just sent. That keeps a
        /// single path from "a move happened" to "the board changed and the stones moved", instead
        /// of a host path and a client path that have to be kept in agreement by hand.
        /// </summary>
        private void ExecuteAuthoritativeMove(int player, int row, int col)
        {
            if (Board == null || !started) return;

            if (awaitingOwnBroadcast)
            {
                if (UnityEngine.Time.unscaledTime - awaitingSince < BroadcastTimeoutSeconds)
                {
                    Debug.LogWarning($"NetworkMatch: player {player} sent a move while the previous one was still in flight. Ignored.");
                    return;
                }

                Debug.LogError(
                    $"NetworkMatch: the previous result never came back after {BroadcastTimeoutSeconds}s. " +
                    "Treating it as lost and accepting this move, rather than leaving the match stuck.");
                awaitingOwnBroadcast = false;
            }

            if (player != CurrentPlayer)
            {
                Debug.LogWarning($"NetworkMatch: player {player} tried to move out of turn (it is player {CurrentPlayer}'s turn). Ignored.");
                return;
            }

            Move move = FindLegalMove(Board, player, row, col);
            if (move == null)
            {
                // The client pre-filters obvious illegality for responsiveness, but it is not
                // trusted to be right — this is the check that actually counts.
                Debug.LogWarning($"NetworkMatch: player {player} requested illegal move ({row},{col}). Ignored.");
                return;
            }

            MoveResult result = engine.ApplyMoveWithResult(Board, move, player);

            awaitingOwnBroadcast = true;
            awaitingSince = UnityEngine.Time.unscaledTime;
            transport.Broadcast(NetProtocol.ToJson(
                new MoveResultMessage(player, move, result.Board, result.CapturedStones)));
        }

        /// <summary>
        /// Both sides applying an authoritative result.
        ///
        /// The packet carries the move and the resulting board, not the hundred-odd landing
        /// positions a long relay chain can produce. The full <see cref="MoveResult"/> is rebuilt
        /// here by replaying the move against the local board, which the deterministic engine
        /// guarantees will match — and the board in the packet is what proves it did.
        /// </summary>
        private void HandleMoveResult(string json)
        {
            MoveResultMessage message = NetProtocol.FromJson<MoveResultMessage>(json);
            if (message?.board == null || message.board.Length != GameBoard.HoleCount) return;
            if (Board == null) return;

            // The move in flight has landed, whichever way it resolves below.
            awaitingOwnBroadcast = false;

            Move move = FindLegalMove(Board, message.player, message.row, message.col);
            if (move == null)
            {
                // We cannot even replay it, so there is nothing to animate. Take the host's board
                // and carry on rather than leaving the two screens showing different games.
                Debug.LogError($"NetworkMatch: authoritative move ({message.row},{message.col}) is not legal on the local board. Snapping to the host's state.");
                AdoptAuthoritativeBoard(message);
                Desynced?.Invoke();
                return;
            }

            MoveResult result = engine.ApplyMoveWithResult(Board, move, message.player);

            if (!SameBoard(result.Board.Board, message.board))
            {
                Debug.LogError(
                    $"NetworkMatch: desync after player {message.player} played ({message.row},{message.col}) — " +
                    $"local captures {result.CapturedStones}, host captures {message.captured}. Snapping to the host's state.");
                AdoptAuthoritativeBoard(message);
                Desynced?.Invoke();
                return;
            }

            Board = result.Board;
            CurrentPlayer = Board.CurrentPlayer;

            BoardStateChanged?.Invoke(Board);
            MoveApplied?.Invoke(result);
        }

        private void AdoptAuthoritativeBoard(MoveResultMessage message)
        {
            int next = message.player == 1 ? 2 : 1;
            Board = BoardFrom(message.board, next);
            CurrentPlayer = next;

            BoardStateChanged?.Invoke(Board);
        }

        private void HandleForfeit(string json)
        {
            ForfeitMessage message = NetProtocol.FromJson<ForfeitMessage>(json);
            if (message == null) return;

            Forfeited?.Invoke(message.player);
        }

        // ── Resuming after a drop ─────────────────────────────────────────

        /// <summary>
        /// The connection is whole again. What that means depends on which seat this is.
        ///
        /// The host holds the only authoritative board, so it simply publishes it and everyone —
        /// itself included — lands on it. A client cannot know whether the host noticed the
        /// interruption at all, so it asks; a host that already broadcast will answer twice, and
        /// the second answer is identical to the first, which is the cheapest possible way to be
        /// certain rather than hopeful.
        ///
        /// Note that this fires on both devices, not only the one that dropped. The player who
        /// stayed connected also spent that time with a board nobody was moving, and re-agreeing
        /// the position costs one packet whether or not it had drifted.
        /// </summary>
        private void HandleTransportResumed()
        {
            if (IsHost) BroadcastResumeState();
            else transport.SendToHost(NetProtocol.ToJson(
                new ResumeRequestMessage(LocalPlayer, hasBoard: started && Board != null)));

            // Raised now rather than when the state comes back, and on both devices. The host has
            // nothing to wait for — it is the authority and its board never left — and making it
            // wait for its own broadcast to return would freeze the player who never dropped, for
            // a round trip, every time the other one hiccuped.
            MatchResumed?.Invoke();
        }

        /// <summary>
        /// Somebody is back and wants the position.
        ///
        /// Normally only the authority answers, and only it should: a client replying with its own
        /// board would be handing a returning player a position nobody validated.
        ///
        /// The exception is the case that made this a v3 protocol. When the request says the sender
        /// has no board, their app was killed rather than merely disconnected — and if we are not
        /// the authority, then they are, and the authority has come back empty-handed. Deferring to
        /// it would leave the only surviving copy of the game sitting on this device while the
        /// other one waited for an answer nobody could give, until the resync deadline called the
        /// match off. So we take over.
        ///
        /// Our board is a validated one despite not being the authority's: every result was
        /// replayed here against the local engine and checked against the host's board before it
        /// was adopted (see <see cref="HandleMoveResult"/>), which is the whole reason a client's
        /// mirror is worth promoting rather than merely worth having.
        /// </summary>
        private void HandleResumeRequest(string json)
        {
            ResumeRequestMessage message = NetProtocol.FromJson<ResumeRequestMessage>(json);
            if (message == null) return;

            // SendToHost broadcasts, so our own request comes back to us.
            if (message.player == LocalPlayer) return;

            if (!IsHost)
            {
                bool weHaveIt = started && Board != null;
                if (message.hasBoard || !weHaveIt)
                {
                    // Either they can pick up where they left off and only the authority owes them
                    // an answer, or neither of us has a position — in which case there is nothing
                    // to send and the match will time out, which is the honest outcome.
                    return;
                }

                Debug.Log($"NetworkMatch: player {message.player} came back with no board, so this device " +
                          "(player " + LocalPlayer + ") is taking over as the authority.");
                IsHost = true;

                BroadcastResumeState();

                // And tell our own game, which the broadcast will not: HandleResumeState ignores a
                // packet from ourselves, and we are now the sender. Without this the device that
                // stayed would sit in the "catching up" state it entered when the other one
                // dropped, waiting for a position that it is itself holding, until the resync
                // deadline called off a match that had in fact just been repaired.
                //
                // The position is unchanged — nothing moved while the other player was away — so
                // this is not adopting anything, it is saying out loud that the wait is over.
                MatchResynced?.Invoke(Board, CurrentPlayer);
                return;
            }

            BroadcastResumeState();
        }

        /// <summary>
        /// Publishes the authoritative position to everyone, the host included.
        ///
        /// Sent even before play begins, when there is no board to send. That case is not skipped,
        /// because "we have not started yet" is itself the answer a player who dropped during the
        /// arrangement phase needs — without it they would be left waiting on a packet that never
        /// comes, which is indistinguishable from still being disconnected.
        /// </summary>
        private void BroadcastResumeState()
        {
            int[] cells = Board != null
                ? (int[])Board.Board.Clone()
                : new int[GameBoard.HoleCount];

            transport.Broadcast(NetProtocol.ToJson(
                new ResumeStateMessage(cells, CurrentPlayer, started && Board != null)));
        }

        /// <summary>
        /// Adopting the host's position after a reconnect.
        ///
        /// Unconditional, unlike <see cref="HandleMoveResult"/>, which replays a move locally and
        /// checks its answer. There is nothing to check against here: this device was away and has
        /// no idea what it missed, so its own board is not evidence of anything and disagreeing
        /// with the host would be the bug rather than the detection of one.
        /// </summary>
        private void HandleResumeState(string json)
        {
            // The host is the authority and has just sent this to itself along with everyone else.
            // Adopting its own packet would be harmless today and wrong in principle — the only
            // board it should ever take is the one it computed.
            if (IsHost) return;

            ResumeStateMessage message = NetProtocol.FromJson<ResumeStateMessage>(json);
            if (message?.board == null || message.board.Length != GameBoard.HoleCount) return;

            if (!message.started)
            {
                // Still arranging. Nothing to restore, and overwriting the formation this player is
                // in the middle of laying out would be worse than leaving them to it.
                return;
            }

            started = true;
            Board = BoardFrom(message.board, message.currentPlayer);
            CurrentPlayer = message.currentPlayer;

            // A move that was in flight when the connection went is not coming back. The position
            // in hand is the position, and the next request is free to proceed from it.
            awaitingOwnBroadcast = false;

            BoardStateChanged?.Invoke(Board);
            MatchResynced?.Invoke(Board, CurrentPlayer);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Finds the legal move for a pit, or null when there isn't one. Validation and construction
        /// in one step, because <see cref="GameEngine.GetLegalMoves"/> is what knows the path index
        /// a <see cref="Move"/> needs — the same approach the local game uses for a tap.
        /// </summary>
        private Move FindLegalMove(GameBoard board, int player, int row, int col)
        {
            List<Move> legal = engine.GetLegalMoves(board, player);
            foreach (Move m in legal)
                if (m.Row == row && m.Col == col) return m;

            return null;
        }

        private static GameBoard BoardFrom(int[] cells, int currentPlayer)
        {
            var board = new GameBoard { CurrentPlayer = currentPlayer };
            Array.Copy(cells, board.Board, GameBoard.HoleCount);
            return board;
        }

        private static bool SameBoard(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;

            return true;
        }
    }
}
