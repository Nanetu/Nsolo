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
        /// Starts connecting without asking for a room yet, so the wait happens while the player is
        /// still reading the screen rather than after they press something. Safe to call repeatedly.
        /// </summary>
        void Prewarm();

        /// <summary>Makes a room with a fresh code, retrying if the code is already taken.</summary>
        void CreateRoom();

        /// <summary>Joins an existing room by code.</summary>
        void JoinRoom(string code);

        /// <summary>
        /// Sends an authoritative message to everyone, the sender included. The host's own screen
        /// reacts to its broadcasts by receiving them back, so both sides run the same code path
        /// from the same packet rather than the host having a shortcut the client does not.
        /// </summary>
        void Broadcast(string json);

        /// <summary>Sends a request to the host alone. Used by the client to ask for a move.</summary>
        void SendToHost(string json);

        /// <summary>Leaves the room and stops raising events. Safe to call when not in a room.</summary>
        void Leave();
    }
}
