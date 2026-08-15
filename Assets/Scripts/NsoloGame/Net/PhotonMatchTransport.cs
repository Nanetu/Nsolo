using System;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace NsoloGame.Net
{
    /// <summary>
    /// The only file in the project that knows Photon exists.
    ///
    /// It implements <see cref="IMatchTransport"/> and nothing more: it carries strings, reports who
    /// is host, and says when the match can start or has ended. It contains no game rules, reads no
    /// message bodies, and never touches a <c>GameBoard</c>. Replacing Photon means writing another
    /// file this size — nothing above the seam would change.
    ///
    /// PUN is used at its lowest useful level: a room, two players, and <c>RaiseEvent</c>. No
    /// PhotonView, no observed components, no scene syncing. The board is 32 ints changing every
    /// fifteen seconds or so, which does not need — and would be poorly served by — object sync.
    /// </summary>
    public class PhotonMatchTransport : MonoBehaviourPunCallbacks, IMatchTransport, IOnEventCallback
    {
        /// <summary>
        /// Photon reserves event codes 200 and above for its own use, so anything below that is
        /// ours. One code is enough: the protocol already names its own message types.
        /// </summary>
        private const byte MatchEventCode = 1;

        /// <summary>
        /// How many times a taken room code is regenerated before giving up. Each retry is a full
        /// server round trip, and with ~887 million codes a genuine collision is vanishingly rare —
        /// several failures in a row means something else is wrong and the player should be told
        /// rather than left watching a spinner.
        /// </summary>
        private const int MaxCodeAttempts = 5;

        private enum Intent { None, Create, Join }

        private Intent intent = Intent.None;
        private string requestedCode;
        private int codeAttempts;

        /// <summary>
        /// Set once we are deliberately tearing the match down, so the disconnect callbacks that
        /// follow a normal Leave() do not raise the "connection lost" modal over a player who simply
        /// pressed the back button.
        /// </summary>
        private bool leavingDeliberately;

        /// <summary>Guards against MatchEnded firing twice as Photon reports the same drop two ways.</summary>
        private bool matchEndedRaised;

        public bool IsHost => PhotonNetwork.IsMasterClient;

        public bool IsReady => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.PlayerCount >= 2;

        public string RoomCode => PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom.Name : null;

        public string LocalPlayerName =>
            string.IsNullOrWhiteSpace(PhotonNetwork.NickName) ? "Player" : PhotonNetwork.NickName;

        /// <summary>
        /// The other player in the room. Photon carries nicknames for us, so this needs no message
        /// of its own — but it is null until they actually join, and a player who never set a
        /// username arrives with an empty one, so both cases fall back rather than showing a blank.
        /// </summary>
        public string OpponentName
        {
            get
            {
                if (!PhotonNetwork.InRoom) return null;

                Player[] others = PhotonNetwork.PlayerListOthers;
                if (others == null || others.Length == 0) return null;

                string name = others[0].NickName;
                return string.IsNullOrWhiteSpace(name) ? "Opponent" : name;
            }
        }

        public event Action<string> MessageReceived;
        public event Action<string> RoomCreated;
        public event Action MatchReady;
        public event Action RoomNotFound;
        public event Action ConnectionFailed;
        public event Action<MatchEndReason> MatchEnded;

        // ── Entry points ──────────────────────────────────────────────────

        /// <summary>
        /// Connects ahead of time with no room in mind.
        ///
        /// Reaching a Photon master server and entering a lobby is most of what "Create Room" waits
        /// for — creating the room itself is quick. Doing it when the Create/Join screen opens means
        /// that wait overlaps with the player reading the screen and deciding, so by the time they
        /// press something we are usually already connected and the room appears at once.
        /// </summary>
        public void Prewarm()
        {
            if (PhotonNetwork.IsConnected || intent != Intent.None) return;

            leavingDeliberately = false;
            matchEndedRaised = false;
            ConnectThenAct();
        }

        public void CreateRoom()
        {
            intent = Intent.Create;
            codeAttempts = 0;
            leavingDeliberately = false;
            matchEndedRaised = false;
            ConnectThenAct();
        }

        public void JoinRoom(string code)
        {
            requestedCode = RoomCode_Normalize(code);
            if (!Net.RoomCode.IsWellFormed(requestedCode))
            {
                // A malformed code cannot name a room, so there is no point spending a round trip to
                // be told so. Same outcome the player would have got, arriving sooner.
                RoomNotFound?.Invoke();
                return;
            }

            intent = Intent.Join;
            leavingDeliberately = false;
            matchEndedRaised = false;
            ConnectThenAct();
        }

        private static string RoomCode_Normalize(string code) => Net.RoomCode.Normalize(code);

        /// <summary>
        /// Photon has to be connected to a master server before rooms exist. Rather than making
        /// every caller sequence that, the intent is remembered and acted on once the connection is
        /// up — and acted on immediately when it already is.
        /// </summary>
        private void ConnectThenAct()
        {
            // Carried into the room and shown to the opponent. Falls back rather than sending an
            // empty nickname, which Photon allows but which reads as a blank in any UI.
            string username = NsoloGame.Unity.ProfileManager.Instance?.Username;
            PhotonNetwork.NickName = string.IsNullOrWhiteSpace(username) ? "Player" : username;

            if (PhotonNetwork.InRoom)
            {
                // Still holding the previous match's room — starting a second game without leaving
                // the first would otherwise sit here forever, because none of the callbacks that
                // act on the intent fire while we are already in a room. Leaving takes us back to
                // the master server, and OnConnectedToMaster picks the intent up from there.
                leavingDeliberately = true;
                PhotonNetwork.LeaveRoom();
                return;
            }

            if (PhotonNetwork.IsConnectedAndReady && PhotonNetwork.InLobby)
            {
                ActOnIntent();
                return;
            }

            if (PhotonNetwork.IsConnected)
            {
                // Connected but still negotiating. OnConnectedToMaster / OnJoinedLobby pick it up.
                return;
            }

            if (!PhotonNetwork.ConnectUsingSettings())
            {
                Debug.LogError("PhotonMatchTransport: ConnectUsingSettings failed — is the App ID set in PhotonServerSettings?");
                intent = Intent.None;
                ConnectionFailed?.Invoke();
            }
        }

        private void ActOnIntent()
        {
            switch (intent)
            {
                case Intent.Create:
                    AttemptCreate();
                    break;
                case Intent.Join:
                    PhotonNetwork.JoinRoom(requestedCode);
                    break;
            }
        }

        private void AttemptCreate()
        {
            codeAttempts++;
            string code = Net.RoomCode.Generate();

            var options = new RoomOptions
            {
                MaxPlayers = 2,

                // Not listed in any lobby: this is a play-with-a-friend room, and the only way in is
                // a code somebody was given. It also means the code is the only thing keeping the
                // room private, which is why it is six characters and not three.
                IsVisible = false,
                IsOpen = true,

                // No persistence in this pass. When reconnect-with-the-same-code is built, PlayerTtl
                // is what keeps a dropped player's seat open and EmptyRoomTtl is what keeps the room
                // alive while nobody is in it — both stay at zero until there is a saved board to
                // come back to, so a stale room can never outlive the game it belonged to.
                PlayerTtl = 0,
                EmptyRoomTtl = 0,
            };

            PhotonNetwork.CreateRoom(code, options);
        }

        // ── Sending ───────────────────────────────────────────────────────

        public void Broadcast(string json)
        {
            if (!PhotonNetwork.InRoom) return;

            PhotonNetwork.RaiseEvent(
                MatchEventCode,
                json,
                new RaiseEventOptions { Receivers = ReceiverGroup.All },
                SendOptions.SendReliable);
        }

        public void SendToHost(string json)
        {
            if (!PhotonNetwork.InRoom) return;

            PhotonNetwork.RaiseEvent(
                MatchEventCode,
                json,
                new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient },
                SendOptions.SendReliable);
        }

        public void Leave()
        {
            intent = Intent.None;
            leavingDeliberately = true;

            if (PhotonNetwork.InRoom) PhotonNetwork.LeaveRoom();
        }

        // ── Photon callbacks ──────────────────────────────────────────────

        public void OnEvent(EventData photonEvent)
        {
            if (photonEvent.Code != MatchEventCode) return;
            if (photonEvent.CustomData is not string json) return;

            MessageReceived?.Invoke(json);
        }

        public override void OnConnectedToMaster()
        {
            // Joining the lobby is not strictly required to create or join a named room, but it is
            // the state PUN reports as "ready", and entering it keeps the flow to a single path.
            if (!PhotonNetwork.InLobby) PhotonNetwork.JoinLobby();
        }

        public override void OnJoinedLobby()
        {
            ActOnIntent();
        }

        public override void OnCreatedRoom()
        {
            intent = Intent.None;
            RoomCreated?.Invoke(PhotonNetwork.CurrentRoom.Name);
        }

        public override void OnCreateRoomFailed(short returnCode, string message)
        {
            if (returnCode == ErrorCode.GameIdAlreadyExists && codeAttempts < MaxCodeAttempts)
            {
                // Somebody already holds this code. Letting the server be the one to say so is the
                // only collision check that cannot race: asking the lobby first would leave a window
                // in which two clients both saw a free code and both took it.
                Debug.Log($"PhotonMatchTransport: room code taken, regenerating (attempt {codeAttempts}).");
                AttemptCreate();
                return;
            }

            Debug.LogWarning($"PhotonMatchTransport: could not create a room — {returnCode} {message}");
            intent = Intent.None;
            ConnectionFailed?.Invoke();
        }

        public override void OnJoinedRoom()
        {
            intent = Intent.None;

            // The creator arrives here alone and waits; the joiner arrives to find someone already
            // in. Only the second arrival means the match can begin.
            if (IsReady) MatchReady?.Invoke();
        }

        public override void OnJoinRoomFailed(short returnCode, string message)
        {
            intent = Intent.None;

            // A code that names nothing, a room that has closed, or one that already has two people
            // in it all mean the same thing to the player: that code did not get them into a game.
            if (returnCode == ErrorCode.GameDoesNotExist ||
                returnCode == ErrorCode.GameClosed ||
                returnCode == ErrorCode.GameFull)
            {
                RoomNotFound?.Invoke();
                return;
            }

            Debug.LogWarning($"PhotonMatchTransport: join failed — {returnCode} {message}");
            ConnectionFailed?.Invoke();
        }

        public override void OnPlayerEnteredRoom(Player newPlayer)
        {
            // Someone arriving means the room is live again, so a previous departure should not go
            // on suppressing the next one — a player can leave the lobby and be replaced.
            matchEndedRaised = false;

            if (IsReady) MatchReady?.Invoke();
        }

        public override void OnPlayerLeftRoom(Player otherPlayer)
        {
            RaiseMatchEnded(MatchEndReason.OpponentLeft);
        }

        public override void OnLeftRoom()
        {
            // Cleared here rather than only in OnDisconnected, because Leave() drops the room
            // without dropping the connection — so the callback that would have reset the flag
            // never runs, and a stale true would silently swallow the next genuine disconnect.
            leavingDeliberately = false;

            // A create or join requested while still in the previous room was parked until the
            // room was released. This is that moment.
            if (intent != Intent.None && PhotonNetwork.IsConnectedAndReady && PhotonNetwork.InLobby)
                ActOnIntent();
        }

        public override void OnDisconnected(DisconnectCause cause)
        {
            if (leavingDeliberately)
            {
                leavingDeliberately = false;
                return;
            }

            // The cause is logged rather than shown. Photon's vocabulary here is accurate and
            // completely unhelpful to a player — "ExceptionOnConnect", "ServerTimeout" — so the
            // modal says the connection dropped and this line is what a developer reads instead.
            Debug.LogWarning($"PhotonMatchTransport: disconnected — {cause}");

            if (intent != Intent.None)
            {
                // Never got as far as a room, so this is a failed attempt to start rather than a
                // match that ended.
                intent = Intent.None;
                ConnectionFailed?.Invoke();
                return;
            }

            RaiseMatchEnded(MatchEndReason.LocalDisconnected);
        }

        private void RaiseMatchEnded(MatchEndReason reason)
        {
            // An opponent dropping usually arrives twice — once as them leaving the room, once as
            // the room emptying — and the modal should go up once.
            if (matchEndedRaised) return;
            matchEndedRaised = true;

            MatchEnded?.Invoke(reason);
        }
    }
}
