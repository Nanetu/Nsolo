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
        /// Host-side: a result has been broadcast and has not come back yet. Until it does,
        /// <see cref="CurrentPlayer"/> still names the player who just moved, so without this a
        /// second request arriving inside that window would pass the turn check and be played twice.
        /// The local agents already only ever offer one move at a time — this is here because the
        /// point of a host is not to depend on the other client behaving.
        /// </summary>
        private bool awaitingOwnBroadcast;

        private readonly Action<string> messageHandler;
        private readonly Action<MatchEndReason> matchEndedHandler;

        public NetworkMatch(IMatchTransport transport, GameEngine engine)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

            // Held as fields so Detach can take them off again. The transport outlives any one
            // match — it sits on GameSystems — so a match that subscribed and went away without
            // unsubscribing would keep handling messages for a game that no longer exists.
            messageHandler = HandleMessage;
            matchEndedHandler = reason => MatchEnded?.Invoke(reason);

            transport.MessageReceived += messageHandler;
            transport.MatchEnded += matchEndedHandler;
        }

        /// <summary>
        /// Stops this match listening. Call when leaving an online game, before starting another.
        /// </summary>
        public void Detach()
        {
            transport.MessageReceived -= messageHandler;
            transport.MatchEnded -= matchEndedHandler;
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
        /// The host has pressed START GAME. Both devices leave the lobby and go to the board to
        /// arrange their stones; no board state exists yet.
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
        /// The two boards disagreed. This should be unreachable — the engine is deterministic and
        /// both sides run the identical build — so it is reported rather than quietly patched over.
        /// The authoritative board is adopted regardless.
        /// </summary>
        public event Action Desynced;

        // ── Setup ─────────────────────────────────────────────────────────

        /// <summary>
        /// Fixes the seat assignment. Called once, when the transport reports both players present.
        /// </summary>
        public void AssignSeats(bool isHost)
        {
            IsHost = isHost;
            LocalPlayer = isHost ? 1 : 2;
            started = false;
            formations[1] = null;
            formations[2] = null;
        }

        /// <summary>
        /// Leaves the lobby and sends both players to arrange their stones. Host only — the client's
        /// START GAME does nothing, which is why the button is only offered to the host.
        /// </summary>
        public void RequestBegin()
        {
            if (!IsHost)
            {
                Debug.LogWarning("NetworkMatch: only the host can start the game.");
                return;
            }

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
                Debug.LogWarning($"NetworkMatch: player {player} sent a move while the previous one was still in flight. Ignored.");
                return;
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
