using System;

namespace NsoloGame.Net
{
    /// <summary>
    /// Why the match ended from the network's point of view. Deliberately coarse: the UI shows one
    /// plain-language message either way, and the distinction exists so the reason can be logged and
    /// so a future reconnect feature can tell "they dropped" from "we dropped" without re-reading
    /// Photon's own error codes.
    /// </summary>
    public enum MatchEndReason
    {
        /// <summary>Our own connection went away.</summary>
        LocalDisconnected,

        /// <summary>The other player's connection went away, or they left the room.</summary>
        OpponentLeft,
    }

    /// <summary>
    /// A connection has dropped, but the match is not over yet — the seat is being held while
    /// whoever lost it tries to get back.
    ///
    /// The distinction this carries is the one the two players experience differently. A player
    /// whose own signal went is watching their phone reconnect and knows exactly what happened; the
    /// one still connected sees nothing at all unless told, and "your opponent is reconnecting" is
    /// a different sentence from "reconnecting". Same event, two readings, so the flag travels with
    /// it rather than each side inferring it.
    /// </summary>
    public readonly struct MatchInterruption
    {
        /// <summary>True when it was our connection that dropped, false when it was theirs.</summary>
        public readonly bool Local;

        /// <summary>How long the seat is held before the match is called off, in seconds.</summary>
        public readonly float GraceSeconds;

        public MatchInterruption(bool local, float graceSeconds)
        {
            Local = local;
            GraceSeconds = graceSeconds;
        }
    }

    /// <summary>
    /// The seam between the game and whatever is carrying its messages.
    ///
    /// This is the whole reason the rest of the online code contains no Photon types. Everything
    /// above it — the protocol, the host-authoritative logic in <see cref="NetworkMatch"/>, the move
    /// and capture rules in GameEngine — deals in strings and game state, and none of it changes if
    /// the carrier does. That is also the hook a persistence layer wants later: replaying a saved
    /// match is an implementation of this interface that reads from a database instead of a socket.
    ///
    /// Implementations are expected to raise every event on Unity's main thread.
    /// </summary>
    public interface IMatchTransport
    {
        /// <summary>Whether this device is the room's authority — the one that runs the real rules.</summary>
        bool IsHost { get; }

        /// <summary>Whether both players are present and messages can flow.</summary>
        bool IsReady { get; }

        /// <summary>The six-character code for the current room, or null outside a room.</summary>
        string RoomCode { get; }

        /// <summary>This player's display name, as the opponent sees it.</summary>
        string LocalPlayerName { get; }

        /// <summary>
        /// The other player's display name, or null before they arrive. Never assume it is set —
        /// a player who never chose a username still has to be addressable.
        /// </summary>
        string OpponentName { get; }

        /// <summary>
        /// An opaque label for how far the connection has got.
        ///
        /// Deliberately a string, and deliberately not interpreted: the only thing asked of it is
        /// whether it has changed since last frame, which is how a connection still working its way
        /// through its stages is told apart from one that has stopped answering. Keeping it opaque
        /// is what stops the transport's own vocabulary leaking up here.
        /// </summary>
        string ConnectionStage { get; }

        /// <summary>A message arrived. The payload is protocol JSON; the transport does not read it.</summary>
        event Action<string> MessageReceived;

        /// <summary>A room exists and is waiting for an opponent. Carries the code to show the creator.</summary>
        event Action<string> RoomCreated;

        /// <summary>Both players are in the room. Fired on both devices.</summary>
        event Action MatchReady;

        /// <summary>
        /// The code did not name a room that could be joined — wrong, expired, or already full.
        /// This is the Room Not Found path, and it is a normal outcome rather than an error.
        /// </summary>
        event Action RoomNotFound;

        /// <summary>Could not create or reach a room at all — no connection, or the service refused.</summary>
        event Action ConnectionFailed;

        /// <summary>The match cannot continue. Drives the disconnect modal.</summary>
        event Action<MatchEndReason> MatchEnded;

        /// <summary>
        /// Somebody's connection dropped and the match is on hold while they come back. Play should
        /// stop, but nothing should be torn down: <see cref="MatchResumed"/> or
        /// <see cref="MatchEnded"/> follows, and only the second of those is final.
        /// </summary>
        event Action<MatchInterruption> MatchInterrupted;

        /// <summary>
        /// Everyone is back in the room and play can continue. Always preceded by
        /// <see cref="MatchInterrupted"/>.
        ///
        /// This says the connection is whole again, not that the two boards agree — a device that
        /// was away missed whatever happened while it was, and neither side knows how much. Putting
        /// that right is the layer above's job (see <c>NetworkMatch</c>'s resume exchange), which is
        /// why this carries no state: the transport has none to carry.
        /// </summary>
        event Action MatchResumed;

        /// <summary>
        /// Starts connecting without asking for a room yet, so the wait happens while the player is
        /// still reading the screen rather than after they press something. Safe to call repeatedly.
        /// </summary>
        void Prewarm();

        /// <summary>Makes a room with a fresh code, retrying if the code is already taken.</summary>
        void CreateRoom();

        /// <summary>Joins an existing room by code.</summary>
        void JoinRoom(string code);

        /// <summary>
        /// Reclaims a seat in a room this device was in before, by a code it saved rather than one
        /// the player typed.
        ///
        /// Distinct from <see cref="JoinRoom"/> and not a convenience wrapper on it. A held seat
        /// still counts against the room's two places, so asking to join one is asking to be a
        /// third player and is liable to be refused; this asks to be the player already in it.
        /// Implementations should also fail rather than fall back to an ordinary join — a returning
        /// player who ends up in the wrong seat, or in a room that merely had space, is worse off
        /// than one who is told the game has ended.
        /// </summary>
        void RejoinRoom(string code);

        /// <summary>
        /// Sends an authoritative message to everyone, the sender included. The host's own screen
        /// reacts to its broadcasts by receiving them back, so both sides run the same code path
        /// from the same packet rather than the host having a shortcut the client does not.
        /// </summary>
        void Broadcast(string json);

        /// <summary>
        /// Sends a request for the host to act on. Used by the client to ask for a move.
        ///
        /// "For the host to act on" rather than "to the host alone": who the host is, is a seat
        /// this game assigned, not a role the carrier decides, and an implementation is free to
        /// deliver this to everyone as long as only the host acts. That distinction is what keeps
        /// authority stable across a reconnect — see <c>PhotonMatchTransport.SendToHost</c>.
        /// </summary>
        void SendToHost(string json);

        /// <summary>
        /// Leaves the room for good and stops raising events. Safe to call when not in a room.
        ///
        /// Final, unlike a dropped connection: this is the player choosing to go, so no seat is
        /// held for them and the opponent is told the match is over rather than being asked to
        /// wait out a grace period nobody is coming back from.
        /// </summary>
        void Leave();
    }
}
