using System;
using NsoloGame.Core;
using UnityEngine;

namespace NsoloGame.Net
{
    /// <summary>
    /// Every message that crosses the wire, and the code that turns them into JSON and back.
    ///
    /// Deliberately free of Photon types. Nothing in this file knows how a string reaches the other
    /// device, which is what lets the same messages be replayed from a database later without the
    /// protocol changing shape.
    ///
    /// JsonUtility is the serializer, which constrains the design in ways worth stating: it handles
    /// [Serializable] classes with public fields and flat arrays, and cannot touch ValueTuple,
    /// Dictionary, or polymorphism. That is why board state travels as a plain int[] rather than as
    /// a <see cref="GameBoard"/>, and why dispatch works by peeking at one field rather than by
    /// deserializing into a base type.
    /// </summary>
    public static class NetProtocol
    {
        /// <summary>
        /// Bumped whenever the shape of a message changes. Both sides check it on the first packet
        /// they receive: two builds of the app with different protocols would otherwise misread each
        /// other's board state and desync in a way that looks like a rules bug.
        ///
        /// v2 added the resume exchange (<see cref="ResumeRequestMessage"/>,
        /// <see cref="ResumeStateMessage"/>) that a reconnecting player uses to get the board back.
        ///
        /// Bumped rather than added quietly, even though a v1 client would simply drop the two new
        /// actions as unknown. "Drop and carry on" is the right behaviour for a message that does
        /// not matter; this one carries the authoritative board, and a client that ignored it would
        /// keep playing a game it had already fallen behind — a desync that looks like a rules bug
        /// rather than a version mismatch. Refusing to speak is the honest failure.
        ///
        /// v3 adds <see cref="ResumeRequestMessage.hasBoard"/>, which is what lets a player come
        /// back after their app was killed rather than merely disconnected.
        ///
        /// The two are not the same problem. A player whose connection dropped still has the whole
        /// game in memory and only needs the moves they missed; a player whose app was killed has
        /// nothing, and if they were the host, nothing is exactly what the authority now holds. v2
        /// let only the host answer a resume request, so a returning host asked a question that the
        /// one device still holding the position was forbidden to answer. The flag says which of
        /// the two kinds of return this is, and the answer follows from it: whoever still has the
        /// board sends it, promoting itself to authority if that is what it takes.
        ///
        /// A v2 build would parse a v3 request as one from a player who has a board — false is the
        /// default for a missing bool — and refuse to answer it, which is the old behaviour and the
        /// wrong one. Hence the bump rather than a silent field.
        /// </summary>
        public const int Version = 3;

        public const string ActionBegin = "begin";
        public const string ActionFormation = "formation";
        public const string ActionStart = "start";
        public const string ActionMove = "move";
        public const string ActionResult = "result";
        public const string ActionForfeit = "forfeit";

        /// <summary>"I am back — somebody tell me where we are." Sent by a player who has rejoined.</summary>
        public const string ActionResumeRequest = "resume?";

        /// <summary>The host's answer: the whole authoritative position.</summary>
        public const string ActionResumeState = "resume";

        [Serializable]
        private class ActionPeek
        {
            public string action;
            public int v;
        }

        /// <summary>
        /// Reads the action name off a message without committing to a type. Returns null when the
        /// string is not a message this build understands, which is treated as a dropped packet
        /// rather than an error — a future version may legitimately send actions we do not know.
        /// </summary>
        public static string PeekAction(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                return JsonUtility.FromJson<ActionPeek>(json)?.action;
            }
            catch (Exception)
            {
                // Malformed JSON. Callers log and ignore; there is nothing useful to salvage.
                return null;
            }
        }

        /// <summary>Reads the protocol version off any message. Returns 0 when absent.</summary>
        public static int PeekVersion(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;

            try
            {
                return JsonUtility.FromJson<ActionPeek>(json)?.v ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static string ToJson<T>(T message) => JsonUtility.ToJson(message);

        /// <summary>
        /// Parses a message of a known type. Returns null rather than throwing, so a corrupt packet
        /// costs one dropped message instead of tearing down the match.
        /// </summary>
        public static T FromJson<T>(string json) where T : class
        {
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"NetProtocol: could not parse message as {typeof(T).Name} — {e.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// A player leaving the lobby and taking the other with them. Carries nothing: it is a signal,
    /// not data — the board does not exist yet, because both players are about to arrange it.
    ///
    /// Either side may send it, and the receiver does not care which did. That is safe precisely
    /// because it carries no state: it opens the arrangement phase, where each player fills in their
    /// own half and neither waits on the other, and every rule that follows still runs on the host.
    /// The receiver acts on the first one and ignores any second, since both players tapping at the
    /// same moment is an ordinary race rather than an error.
    /// </summary>
    [Serializable]
    public class BeginMessage
    {
        public string action = NetProtocol.ActionBegin;
        public int v = NetProtocol.Version;
    }

    /// <summary>
    /// One player's opening formation: the 16 pits on their own side, in row-major order over their
    /// two rows. Player 1's index 0 is (0,0) and player 2's is (2,0), so neither side has to know
    /// the other's row numbers to fill this in.
    /// </summary>
    [Serializable]
    public class FormationMessage
    {
        public string action = NetProtocol.ActionFormation;
        public int v = NetProtocol.Version;
        public int player;
        public int[] cells;

        public FormationMessage() { }

        public FormationMessage(int player, int[] cells)
        {
            this.player = player;
            this.cells = cells;
        }
    }

    /// <summary>
    /// The host's "we are under way" packet: the assembled 32-pit board both sides start from, and
    /// which seat opens. Sent once, after both formations are in.
    /// </summary>
    [Serializable]
    public class StartMessage
    {
        public string action = NetProtocol.ActionStart;
        public int v = NetProtocol.Version;
        public int[] board;
        public int firstPlayer;

        public StartMessage() { }

        public StartMessage(int[] board, int firstPlayer)
        {
            this.board = board;
            this.firstPlayer = firstPlayer;
        }
    }

    /// <summary>
    /// A move request from the client to the host: "I would like to play this pit."
    ///
    /// A Nsolo move is one pit and nothing else — there is no destination, because where the stones
    /// land is computed by <see cref="GameEngine"/> from the pit and the board. The client sends the
    /// pit and waits; it does not compute or predict the outcome.
    /// </summary>
    [Serializable]
    public class MoveRequestMessage
    {
        public string action = NetProtocol.ActionMove;
        public int v = NetProtocol.Version;
        public int player;
        public int row;
        public int col;

        public MoveRequestMessage() { }

        public MoveRequestMessage(int player, int row, int col)
        {
            this.player = player;
            this.row = row;
            this.col = col;
        }
    }

    /// <summary>
    /// The host's authoritative outcome for one move, broadcast to both sides — including back to
    /// the host's own screen, so neither client animates anything that did not come from here.
    ///
    /// It carries the move and the resulting board rather than the full landing sequence. That is a
    /// deliberate trade: <see cref="GameEngine"/> is deterministic and stateless, so a receiver can
    /// rebuild the entire sowing animation by replaying the move against its own copy of the board,
    /// and then check its answer against <see cref="board"/>. A long relay chain can visit a hundred
    /// pits; sending the pit and the result is a fraction of the size and doubles as a desync check.
    ///
    /// <see cref="captured"/> is sent even though the receiver recomputes it, because it is what the
    /// mismatch check reports on — a disagreement about captures is the failure worth naming.
    /// </summary>
    [Serializable]
    public class MoveResultMessage
    {
        public string action = NetProtocol.ActionResult;
        public int v = NetProtocol.Version;
        public int player;
        public int row;
        public int col;
        public int[] board;
        public int captured;

        public MoveResultMessage() { }

        public MoveResultMessage(int player, Move move, GameBoard resulting, int captured)
        {
            this.player = player;
            row = move.Row;
            col = move.Col;
            board = (int[])resulting.Board.Clone();
            this.captured = captured;
        }
    }

    /// <summary>
    /// A player announcing that they are back after a dropped connection, and asking for the
    /// position.
    ///
    /// Carries no board of its own, deliberately. The whole point of asking is that this device
    /// does not know what happened while it was away — anything it sent about its own state would
    /// be a guess, and a guess is exactly what must not reach the authority.
    ///
    /// <see cref="hasBoard"/> is not a board, though: it is the one bit about our own state that is
    /// not a guess, and the receiver needs it to know which kind of return this is. A player who
    /// merely reconnected still has the position and is only asking to be brought up to date; one
    /// whose app was killed and restarted has nothing at all. When the second of those was the
    /// host, the other device is the only one left that knows where the game got to, and this flag
    /// is what tells it to answer rather than defer to an authority that cannot.
    /// </summary>
    [Serializable]
    public class ResumeRequestMessage
    {
        public string action = NetProtocol.ActionResumeRequest;
        public int v = NetProtocol.Version;
        public int player;

        /// <summary>Whether the sender still holds a position, or is coming back from nothing.</summary>
        public bool hasBoard;

        public ResumeRequestMessage() { }

        public ResumeRequestMessage(int player, bool hasBoard)
        {
            this.player = player;
            this.hasBoard = hasBoard;
        }
    }

    /// <summary>
    /// The host putting a returning player back in the game: the current board, whose turn it is,
    /// and whether play has actually started.
    ///
    /// This is a full position rather than the moves that were missed, because the host cannot know
    /// how far behind the other device is — it may have missed one move or the entire game, and it
    /// has no way to say which. A whole board is a few hundred bytes and is correct in every one of
    /// those cases; a replay would need history nobody is keeping and would still have to end with
    /// this to prove it worked.
    ///
    /// <see cref="started"/> distinguishes the two phases that look alike from a distance. A player
    /// who dropped while arranging their stones comes back to a board with no agreed position yet,
    /// and must not be shown a live game; one who dropped mid-play must not be sent back to arrange.
    /// </summary>
    [Serializable]
    public class ResumeStateMessage
    {
        public string action = NetProtocol.ActionResumeState;
        public int v = NetProtocol.Version;
        public int[] board;
        public int currentPlayer;
        public bool started;

        public ResumeStateMessage() { }

        public ResumeStateMessage(int[] board, int currentPlayer, bool started)
        {
            this.board = board;
            this.currentPlayer = currentPlayer;
            this.started = started;
        }
    }

    /// <summary>
    /// A player conceding. Carries who gave up rather than who won, so the receiver derives the
    /// winner the same way the local game does and there is one less thing to disagree about.
    /// </summary>
    [Serializable]
    public class ForfeitMessage
    {
        public string action = NetProtocol.ActionForfeit;
        public int v = NetProtocol.Version;
        public int player;

        public ForfeitMessage() { }

        public ForfeitMessage(int player)
        {
            this.player = player;
        }
    }
}
