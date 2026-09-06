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

        /// <summary>
        /// How long the connection is held open while the app is in the background, in seconds.
        ///
        /// Sharing a room code means leaving the game: open WhatsApp, find the person, paste, send,
        /// come back. PUN's default allowance for that is sixty seconds, which is a realistic amount
        /// of time to spend picking a contact — and running out of it means coming back to a dead
        /// room and a code you have already sent someone. Five minutes covers the errand with room
        /// to spare. Nothing is being kept alive that a player is not actively coming back to: the
        /// timer only runs while the app is backgrounded, and the room still closes as soon as they
        /// actually leave.
        /// </summary>
        private const float BackgroundKeepAliveSeconds = 300f;

        /// <summary>
        /// How long a dropped player's seat is held open before the match is called off.
        ///
        /// Phones lose connections for reasons that have nothing to do with wanting to stop playing:
        /// a lift, a tunnel, a call coming in, a handover between wifi and mobile data. Ending the
        /// match on the first dropped packet turned every one of those into a lost game, which is
        /// the harshest possible reading of a network that is simply normal.
        ///
        /// Five minutes is the ceiling Photon allows for <c>EmptyRoomTtl</c>, and it is also about
        /// as long as a player will sit looking at a "reconnecting" message before giving up on
        /// their own. Both sides can leave deliberately at any point in it — the wait is a chance to
        /// come back, not a sentence.
        /// </summary>
        public const float RejoinGraceSeconds = 300f;

        /// <summary>Photon states its room lifetimes in milliseconds.</summary>
        private static int GraceMilliseconds => (int)(RejoinGraceSeconds * 1000f);

        /// <summary>
        /// Where this device's Photon identity is kept between runs.
        ///
        /// Rejoining is only meaningful if the server can tell that the player asking to come back
        /// is the one whose seat is being held, and that identity is the UserId. Left to itself PUN
        /// generates a fresh one per connection, so a reconnecting player would arrive as a stranger
        /// and be refused a room that already has two people in it. Persisting it means a player who
        /// drops out — or whose app is killed outright and restarted — is still the same player.
        /// </summary>
        private const string UserIdKey = "PhotonUserId";

        private enum Intent { None, Create, Join, Rejoin }

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

        /// <summary>
        /// When the current grace period runs out, on <see cref="Time.realtimeSinceStartup"/>, or
        /// zero when nothing is being waited for.
        ///
        /// Real time rather than scaled or even unscaled game time: this is a wall-clock promise
        /// made to the other device, and it has to mean the same number of seconds on both — a
        /// paused editor or a stalled frame must not buy anyone extra.
        /// </summary>
        private float graceExpiresAt;

        /// <summary>Which kind of interruption is being waited out, for the report when it expires.</summary>
        private bool graceIsLocal;

        /// <summary>
        /// Set while we are trying to get back into a room we dropped out of, so the disconnect
        /// callbacks that arrive during the attempt are not read as a fresh failure.
        /// </summary>
        private bool rejoining;

        /// <summary>
        /// The room we were in when the connection went, so a reconnect knows what to ask for.
        /// PhotonNetwork.CurrentRoom is gone by the time OnDisconnected runs.
        /// </summary>
        private string rejoinCode;

        private bool Waiting => graceExpiresAt > 0f;

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
        /// <summary>PUN's own connection state, passed up as a label rather than a type.</summary>
        public string ConnectionStage => PhotonNetwork.NetworkClientState.ToString();

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
        public event Action<MatchInterruption> MatchInterrupted;
        public event Action MatchResumed;

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

            BeginFreshAttempt();
            ConnectThenAct();
        }

        public void CreateRoom()
        {
            intent = Intent.Create;
            codeAttempts = 0;
            BeginFreshAttempt();
            ConnectThenAct();
        }

        /// <summary>
        /// Clears everything the previous room left behind, so a new one starts from a known state.
        ///
        /// The rejoin state is part of that and easy to miss: a player who drops, gives up waiting
        /// and starts a fresh game would otherwise carry the old room's code and an unexpired grace
        /// timer into it, and the first hiccup in the new match would send them back to the old one.
        /// </summary>
        private void BeginFreshAttempt()
        {
            leavingDeliberately = false;
            matchEndedRaised = false;
            rejoining = false;
            rejoinCode = null;
            ClearGrace();
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
            BeginFreshAttempt();
            ConnectThenAct();
        }

        /// <summary>
        /// Reclaims a seat in a room this device was already in, by a code it saved rather than one
        /// the player typed.
        ///
        /// Separate from <see cref="JoinRoom"/> because the request to the server is a different
        /// one — see <c>ActOnIntent</c>. Separate from the automatic reconnect in
        /// <see cref="TryRejoin"/> too, and for a plainer reason: that one resumes a cached
        /// connection, and this is called when there is no connection to resume because the app was
        /// killed and started again.
        /// </summary>
        public void RejoinRoom(string code)
        {
            requestedCode = RoomCode_Normalize(code);
            if (!Net.RoomCode.IsWellFormed(requestedCode))
            {
                RoomNotFound?.Invoke();
                return;
            }

            intent = Intent.Rejoin;
            BeginFreshAttempt();
            ConnectThenAct();
        }

        private static string RoomCode_Normalize(string code) => Net.RoomCode.Normalize(code);

        /// <summary>
        /// Gives this device a Photon identity that survives a disconnect, a restart, and a new
        /// build.
        ///
        /// Kept in PlayerPrefs rather than derived from the device, because every stable
        /// device-side identifier is either unavailable or a privacy problem on one of the two
        /// platforms this ships to. A GUID answers the only question actually being asked — "is
        /// this the same player as a minute ago?" — and answers nothing else about them.
        ///
        /// Deliberately not the username. Two players called "Player" would share an identity, and
        /// the server would treat the second one's arrival as the first one reconnecting.
        /// </summary>
        private static void EnsureStableUserId()
        {
            string id = PlayerPrefs.GetString(UserIdKey, null);

            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(UserIdKey, id);
                PlayerPrefs.Save();
            }

            // Left alone once set: replacing AuthValues mid-session would change who we are to the
            // server, which is exactly what a held seat is keyed on.
            if (PhotonNetwork.AuthValues != null && PhotonNetwork.AuthValues.UserId == id) return;

            PhotonNetwork.AuthValues = new AuthenticationValues { UserId = id };
        }

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

            // Asserted before connecting, because the identity is established as part of the
            // handshake and setting it afterwards would apply to the connection after this one.
            EnsureStableUserId();

            // Set here rather than at startup so a player who never goes online never causes PUN's
            // handler object to be created. It is idempotent, so every path through this method can
            // safely assert it.
            PhotonNetwork.KeepAliveInBackground = BackgroundKeepAliveSeconds;

            // Keep reading the network even if something stops the clock.
            //
            // PUN dispatches incoming messages from FixedUpdate, and Unity does not call
            // FixedUpdate at all while Time.timeScale is zero — so at zero, nothing is received.
            // Not "received late": not received. Moves, and the opponent leaving, sit in the queue
            // unseen until the clock starts again, and then arrive all at once.
            //
            // Nothing in an online game is supposed to stop the clock any more — pause deliberately
            // does not, and the in-game tips are held back entirely (TutorialCoach.Suppressed) —
            // but "supposed to" is a poor thing to rest a connection on, and this is PUN's own
            // switch for it. At zero the dispatch moves to LateUpdate, which Unity always calls.
            PhotonNetwork.MinimalTimeScaleToDispatchInFixedUpdate = 0f;

            if (PhotonNetwork.InRoom)
            {
                // Still holding the previous match's room — starting a second game without leaving
                // the first would otherwise sit here forever, because none of the callbacks that
                // act on the intent fire while we are already in a room. Leaving takes us back to
                // the master server, and OnConnectedToMaster picks the intent up from there.
                //
                // Final, like Leave(): the player is starting another game, so the seat in the old
                // room should not be held open for a return that is never coming.
                leavingDeliberately = true;
                ClearGrace();
                PhotonNetwork.LeaveRoom(becomeInactive: false);
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
                return;
            }

            // Asserted immediately after connecting, which PUN explicitly supports, and which
            // overrides whatever AppVersion happens to be sitting in PhotonServerSettings.
            PhotonNetwork.GameVersion = NetworkProtocolVersion;
        }

        /// <summary>
        /// What this build speaks on the wire, and the only thing that should ever partition players
        /// from each other.
        ///
        /// Photon puts clients with different AppVersions into separate virtual applications: they
        /// connect fine and see none of each other's rooms, which surfaces as "room not found" for a
        /// code that certainly exists. PUN builds that AppVersion out of
        /// <see cref="PhotonNetwork.GameVersion"/>, and <c>ConnectUsingSettings</c> takes
        /// GameVersion from <c>PhotonServerSettings.AppVersion</c> — a field the Photon wizard
        /// edits, and which has nothing to do with whether two builds can actually understand one
        /// another. Editing UI, art or menus therefore had every ability to cut players off from
        /// their friends, which is not a property anybody would choose.
        ///
        /// Pinning it here decouples the two. Bump this ONLY when a change would genuinely break an
        /// older client — the event codes or payload shape in <see cref="NetProtocol"/> — and never
        /// for a version number, a UI rebuild or a store release.
        /// </summary>
        /// <remarks>
        /// Bumped to 2 for the rejoin work. This is one of the rare changes that earns it: a v1
        /// build holds no seat open, so a v1 player who dropped would be gone for good while the v2
        /// player opposite them waited out five minutes for somebody the server had already
        /// forgotten — and v1 does not understand the resume exchange that puts a returning player
        /// back on the right board. Keeping the two apart is kinder than letting them half-play.
        /// </remarks>
        private const string NetworkProtocolVersion = "nsolo-net-2";

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
                case Intent.Rejoin:
                    // Not JoinRoom, which is a different request to the server and the wrong one.
                    //
                    // The room holds two seats and PlayerTtl keeps the dropped player's actor in
                    // one of them while it is being held — so as far as MaxPlayers is concerned the
                    // room is still full, and an ordinary join is liable to be turned away as
                    // GameFull by the very seat it is trying to reclaim. RejoinRoom says what is
                    // actually being asked: not "let me in" but "I am the player in that seat".
                    //
                    // It also fails in the right way. Where a join would wander into a room that
                    // happened to have space, this is refused outright once the seat has gone,
                    // which is exactly the answer a returning player needs.
                    PhotonNetwork.RejoinRoom(requestedCode);
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

                // How long a dropped player may be gone before the room forgets them. This is what
                // makes a seat something to come back to rather than something to lose: while it
                // runs, the server keeps the actor number and the room stays willing to readmit it,
                // so a rejoin is the same player returning rather than a third one arriving at a
                // room that already holds two.
                PlayerTtl = GraceMilliseconds,

                // And how long the room itself outlives the last player in it. Needed for the case
                // the player one is about to hit: both devices dropping, or the only remaining
                // player dropping while waiting for the other. Without it the room is destroyed the
                // instant it empties and there is nothing left to rejoin, which would make the seat
                // above a promise the room could not keep.
                //
                // Five minutes is Photon's ceiling for this value; the seat timer is set to match so
                // neither outlives the other.
                EmptyRoomTtl = GraceMilliseconds,
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

            // Sent to everyone, not to Photon's master client, even though only the host acts on it.
            //
            // These two are not the same player any more once a reconnect is possible. Photon hands
            // the master role to whoever is left the moment the holder drops, and hands it back to
            // nobody when they return — so a host who spent thirty seconds in a lift comes back as
            // an ordinary client while still holding the authoritative board and seat 1. Addressing
            // MasterClient would have routed every move request to the player who cannot answer it,
            // and the match would hang with both sides waiting on each other.
            //
            // Authority is the seat, which this game assigned and which nothing on the network can
            // reassign. NetworkMatch already drops these unless it is the host, so broadcasting
            // costs one ignored packet on the other device and removes the coupling entirely.
            Broadcast(json);
        }

        public void Leave()
        {
            intent = Intent.None;
            leavingDeliberately = true;
            ClearGrace();

            // Explicitly *not* becoming inactive, which is what PUN does by default once PlayerTtl
            // is set. This is the player choosing to leave, and holding their seat for five minutes
            // afterwards would leave the opponent watching a "reconnecting" countdown for somebody
            // who is already back at the main menu. A drop is worth waiting out; a decision is not.
            if (PhotonNetwork.InRoom) PhotonNetwork.LeaveRoom(becomeInactive: false);
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
            // A reconnect is already on its way to a specific room and must be left alone. PUN
            // reaches the master server as part of ReconnectAndRejoin, so this callback fires
            // mid-rejoin — and sending it to the lobby here would abandon the room it was going to,
            // turning every recoverable drop into a lost match.
            if (rejoining) return;

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
            rejoinCode = PhotonNetwork.CurrentRoom?.Name;

            // Back in the room we dropped out of. This is a resumption, not an arrival: the game
            // above still has its board and its seat, and telling it a match was "ready" would
            // start a second one on top of the one already in progress.
            if (rejoining)
            {
                rejoining = false;
                ClearGrace();
                MatchResumed?.Invoke();
                return;
            }

            // The creator arrives here alone and waits; the joiner arrives to find someone already
            // in. Only the second arrival means the match can begin.
            if (IsReady) MatchReady?.Invoke();
        }

        public override void OnJoinRoomFailed(short returnCode, string message)
        {
            intent = Intent.None;

            // A failed *rejoin* is a different event entirely, and must not be reported as a bad
            // code. The player typed nothing — they were already in this room, and it is gone: the
            // seat timed out, or the opponent left and took the empty room with it. Sending them to
            // "Room Not Found", which offers to retry with a code they never had, would be a dead
            // end. This is the match ending.
            if (rejoining)
            {
                rejoining = false;
                ClearGrace();
                Debug.Log($"PhotonMatchTransport: the room we dropped out of is gone — {returnCode} {message}");
                RaiseMatchEnded(MatchEndReason.LocalDisconnected);
                return;
            }

            // A code that names nothing, a room that has closed, or one that already has two people
            // in it all mean the same thing to the player: that code did not get them into a game.
            //
            // The last two are what a refused RejoinRoom comes back as: JoinFailedFoundActiveJoiner
            // when somebody is already sitting in the seat, JoinFailedWithRejoinerNotFound when the
            // seat has gone. Both mean the game is not there to be walked back into, which is the
            // same sentence, so they are reported the same way and the caller decides how to word
            // it — see OnlineFlowController.AbandonRejoin.
            if (returnCode == ErrorCode.GameDoesNotExist ||
                returnCode == ErrorCode.GameClosed ||
                returnCode == ErrorCode.GameFull ||
                returnCode == ErrorCode.JoinFailedFoundActiveJoiner ||
                returnCode == ErrorCode.JoinFailedWithRejoinerNotFound)
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

            // The opponent we were holding a seat for has taken it back. Raised before MatchReady
            // so the layer above hears "the interruption is over" rather than "a new player has
            // arrived", which would send both devices back to the lobby mid-game.
            if (Waiting && !graceIsLocal)
            {
                ClearGrace();
                MatchResumed?.Invoke();
                return;
            }

            if (IsReady) MatchReady?.Invoke();
        }

        /// <summary>
        /// Somebody is no longer in the room. Whether that ends the match depends entirely on which
        /// of the two ways it happened.
        ///
        /// Photon draws exactly the distinction that matters here and puts it on the player it
        /// hands us. An <c>IsInactive</c> player has dropped and their seat is being held for
        /// <see cref="RejoinGraceSeconds"/>; one that is not has actually left, either by pressing
        /// a button or by their seat timing out. Before this, both arrived here as the same
        /// callback and both ended the match — which is why a lift, a tunnel or a call coming in
        /// cost somebody a game they were winning.
        /// </summary>
        public override void OnPlayerLeftRoom(Player otherPlayer)
        {
            if (otherPlayer != null && otherPlayer.IsInactive)
            {
                BeginGrace(local: false);
                return;
            }

            RaiseMatchEnded(MatchEndReason.OpponentLeft);
        }

        public override void OnLeftRoom()
        {
            // Cleared here rather than only in OnDisconnected, because Leave() drops the room
            // without dropping the connection — so the callback that would have reset the flag
            // never runs, and a stale true would silently swallow the next genuine disconnect.
            leavingDeliberately = false;

            // No room to go back to. Without this, a connection dropping later — from the menu,
            // with no game in progress — would find a stale code here and spend five minutes
            // trying to rejoin a match that ended long ago.
            rejoinCode = null;

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

            // We were in a room, so there is a seat waiting and it is worth trying to get back to.
            // The seat is held for RejoinGraceSeconds whether or not we manage it, so the only cost
            // of trying is the attempt itself.
            if (!string.IsNullOrEmpty(rejoinCode) && TryRejoin()) return;

            // An attempt that could not even be started, while the seat is still being held. This
            // is a retry failing, not the first drop — the window is already running, and ending
            // the match here would throw away whatever is left of it over one bad moment on a
            // connection that is by definition unreliable. Update ends it when the time is actually
            // up, which is the only thing that should.
            if (Waiting && graceIsLocal) return;

            RaiseMatchEnded(MatchEndReason.LocalDisconnected);
        }

        /// <summary>
        /// Asks PUN to reconnect and take our seat back, and reports whether the attempt started.
        ///
        /// <c>ReconnectAndRejoin</c> is the right call rather than a fresh connect-and-join: it
        /// reuses the cached server address and the room we were in, and it is the path that
        /// presents us as the returning player rather than as a new one — which matters because the
        /// room already holds two seats and would refuse a third.
        ///
        /// A false return is not a failure to report on its own. It means PUN had nothing to
        /// reconnect with, which is the ordinary case when the app was killed rather than
        /// interrupted, and the caller falls through to ending the match.
        /// </summary>
        private bool TryRejoin()
        {
            // Asked before anything is announced. Raising the interruption first and withdrawing it
            // on failure would put "Reconnecting" on screen for the one frame before "the match is
            // over", which tells the player something is being attempted and then immediately that
            // it never was.
            if (!PhotonNetwork.ReconnectAndRejoin())
            {
                Debug.LogWarning("PhotonMatchTransport: could not start a rejoin — no cached connection to resume.");
                return false;
            }

            rejoining = true;
            BeginGrace(local: true);
            return true;
        }

        // ── Grace period ──────────────────────────────────────────────────

        /// <summary>
        /// Starts holding the match open, and says so once. Repeat calls while a wait is already
        /// running are ignored rather than restarting the clock — a connection that drops in stages
        /// reports itself several times, and each report must not buy another five minutes.
        /// </summary>
        private void BeginGrace(bool local)
        {
            if (Waiting) return;

            graceIsLocal = local;
            graceExpiresAt = Time.realtimeSinceStartup + RejoinGraceSeconds;

            MatchInterrupted?.Invoke(new MatchInterruption(local, RejoinGraceSeconds));
        }

        private void ClearGrace()
        {
            graceExpiresAt = 0f;
            graceIsLocal = false;
        }

        /// <summary>
        /// Ends the match when nobody came back in time.
        ///
        /// Polled rather than scheduled because the thing being waited on is a wall clock, and a
        /// coroutine would stop with the object, stop with the scene, and quietly not run at all on
        /// a device that suspended the app — which is precisely the situation this measures.
        /// </summary>
        private void Update()
        {
            if (!Waiting) return;
            if (Time.realtimeSinceStartup < graceExpiresAt) return;

            bool local = graceIsLocal;
            ClearGrace();
            rejoining = false;

            // The room this pointed at has outlived its usefulness — the seat is gone and, if we
            // were the last one in it, so is the room. Left set, a disconnect from the menu later
            // would send us chasing it.
            rejoinCode = null;

            Debug.Log($"PhotonMatchTransport: rejoin window closed after {RejoinGraceSeconds:0}s.");

            // Reported as an ordinary disconnect, because by now that is what it is. The grace
            // period was the difference between the two, and it has run out.
            RaiseMatchEnded(local ? MatchEndReason.LocalDisconnected : MatchEndReason.OpponentLeft);
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
