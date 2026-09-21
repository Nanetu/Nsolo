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

        /// <summary>
        /// When to try getting back in again, on <see cref="Time.realtimeSinceStartup"/>. Only
        /// meaningful while a local grace window is open.
        /// </summary>
        private float nextRejoinAttemptAt;

        /// <summary>
        /// How long to leave between attempts to get back into the room.
        ///
        /// The thing being waited for is a network coming back, which happens on its own schedule
        /// and gives no notice — so this is a poll, and the interval is a trade between noticing
        /// quickly and hammering a connection that is not there. Five seconds gives about sixty
        /// attempts across the window, which is far more than one and far less than a spin.
        /// </summary>
        private const float RejoinRetrySeconds = 5f;

        /// <summary>When the attempt currently in flight began, or zero when none is.</summary>
        private float rejoinAttemptStartedAt;

        /// <summary>
        /// How long an attempt may be in flight before another is allowed over the top of it.
        ///
        /// The in-flight flag is cleared by an outcome — joined, refused, or disconnected — and PUN
        /// delivers one of those in the ordinary case. The case this covers is the connection
        /// neither succeeding nor failing: a socket opened into a network that has since gone,
        /// which sits in "connecting" indefinitely and reports nothing. Left to itself that flag
        /// would hold off every remaining retry in the window, which is the same failure this whole
        /// change exists to remove, arrived at from the other side.
        /// </summary>
        private const float RejoinAttemptTimeoutSeconds = 15f;

        /// <summary>
        /// When to ask again for a saved match's seat that the server said was still occupied, on
        /// <see cref="Time.realtimeSinceStartup"/>, or zero when nothing is pending.
        /// </summary>
        private float seatRetryAt;

        /// <summary>After this, a seat still reported as occupied is taken at its word.</summary>
        private float seatRetryGiveUpAt;

        /// <summary>
        /// How long to wait before asking again for a seat the server says is still occupied.
        ///
        /// That answer means the server has not noticed yet that our previous connection is dead —
        /// the app was killed and relaunched, or the network changed under it, faster than the
        /// server's own timeout. It notices within seconds, so a short wait is all it takes.
        /// </summary>
        private const float SeatRetrySeconds = 3f;

        /// <summary>Comfortably past the server's timeout for a dead connection.</summary>
        private const float SeatRetryPatienceSeconds = 30f;

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
            seatRetryAt = 0f;
            seatRetryGiveUpAt = 0f;
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

            // On the master server outside the lobby, and idle. PUN leaves us here after the game
            // server turns a join away: it goes back to the master and reports the failure *instead
            // of* OnConnectedToMaster, so the callback that would normally lead into the lobby has
            // already been skipped — and waiting for it below would wait forever.
            if (PhotonNetwork.NetworkClientState == ClientState.ConnectedToMasterServer)
            {
                PhotonNetwork.JoinLobby();
                return;
            }

            if (PhotonNetwork.IsConnected)
            {
                // Connected but still negotiating. OnConnectedToMaster / OnJoinedLobby pick it up.
                return;
            }

            if (!PhotonNetwork.ConnectUsingSettings())
            {
                intent = Intent.None;

                // Mid-rejoin this is an attempt that could not start, not a match that is over. The
                // seat is still held and Update will try again in a few seconds; raising
                // ConnectionFailed here would put a "No Connection — TRY AGAIN / MAIN MENU" modal
                // over a game that is still perfectly recoverable, and leave the in-flight flag set
                // so that nothing ever tried again.
                if (Waiting && graceIsLocal)
                {
                    rejoining = false;
                    return;
                }

                Debug.LogError("PhotonMatchTransport: ConnectUsingSettings failed — is the App ID set in PhotonServerSettings?");
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
            seatRetryAt = 0f;
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
            //
            // The intent is what tells the two kinds of rejoin apart. ReconnectAndRejoin carries
            // its own destination and sets none; the from-scratch fallback in TryRejoin has no
            // destination of its own and reaches its room through the lobby like any other join,
            // so it must not be stopped here.
            if (rejoining && intent == Intent.None) return;

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

                // Back in the room, but not necessarily back to a game. The opponent may have
                // dropped or left while we were away, and we could not have heard about it —
                // nothing reaches a device that is offline. Resuming regardless had a client ask an
                // absent host for the position, and the match ended when nobody answered.
                Player[] others = PhotonNetwork.PlayerListOthers;
                if (others.Length == 0)
                {
                    // Gone for good: they left, or their own seat ran out while we were away.
                    RaiseMatchEnded(MatchEndReason.OpponentLeft);
                    return;
                }

                if (others[0].IsInactive)
                {
                    // Dropped, and their seat is still held. Now they are the one being waited
                    // for, and they come back through OnPlayerEnteredRoom, which resumes both
                    // devices together.
                    BeginGrace(local: false);
                    return;
                }

                MatchResumed?.Invoke();
                return;
            }

            // The creator arrives here alone and waits; the joiner arrives to find someone already
            // in. Only the second arrival means the match can begin.
            if (IsReady) MatchReady?.Invoke();
        }

        public override void OnJoinRoomFailed(short returnCode, string message)
        {
            bool askedForSeat = intent == Intent.Rejoin;
            intent = Intent.None;

            // "Somebody is already in that seat" — and the somebody is us. The server has not yet
            // noticed that our previous connection is dead, which is exactly what coming back
            // quickly looks like: wifi handing over to mobile data, or an app killed and relaunched
            // inside the server's own timeout. It is the least final answer a rejoin can get, and
            // both paths below used to read it as the room being gone and end the match on the spot.
            if (returnCode == ErrorCode.JoinFailedFoundActiveJoiner && (rejoining || askedForSeat))
            {
                if (rejoining && Waiting && graceIsLocal)
                {
                    // The reconnect loop in Update asks again on its own schedule.
                    Debug.Log("PhotonMatchTransport: the server still has our old connection in the seat; retrying.");
                    rejoining = false;
                    return;
                }

                if (!rejoining)
                {
                    // A saved match being reclaimed after a relaunch. No grace window runs for
                    // this one, so it keeps a short one of its own.
                    float now = Time.realtimeSinceStartup;
                    if (seatRetryGiveUpAt <= 0f) seatRetryGiveUpAt = now + SeatRetryPatienceSeconds;

                    if (now < seatRetryGiveUpAt)
                    {
                        Debug.Log("PhotonMatchTransport: the server still has our old connection in the seat; retrying.");
                        intent = Intent.Rejoin;
                        seatRetryAt = now + SeatRetrySeconds;
                        return;
                    }
                }
            }

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

        /// <summary>
        /// We are out of the room — which happens for two completely different reasons, and this
        /// callback cannot tell them apart on its own.
        ///
        /// The obvious one is a deliberate <see cref="Leave"/>: the room is released, the
        /// connection stays up, and there is nothing to come back to.
        ///
        /// The other is a dropped connection, and it is the reason this method reads the way it
        /// does. Photon raises OnLeftRoom on its way through a disconnect as well — see
        /// LoadBalancingClient's StatusCode.Disconnect case, which calls
        /// <c>MatchMakingCallbackTargets.OnLeftRoom()</c> and only then
        /// <c>ConnectionCallbackTargets.OnDisconnected()</c>. So on every real drop this ran first
        /// and cleared <see cref="rejoinCode"/>, and <see cref="OnDisconnected"/> arrived a moment
        /// later to find no room worth going back to.
        ///
        /// That one line was the whole of the reported bug. The device that dropped fell straight
        /// through to <see cref="RaiseMatchEnded"/> and put up "the match can't continue" with no
        /// way back, while the device that stayed — which reaches its own grace period through
        /// <see cref="OnPlayerLeftRoom"/>, a path the wipe never touched — sat counting down five
        /// minutes for a player whose app had already given up. Same event, opposite outcomes, and
        /// the seat the server was faithfully holding was never once asked for: every retry in
        /// <see cref="Update"/> is guarded on a rejoinCode that could no longer be set. The rejoin
        /// feature was unreachable in practice rather than merely unreliable.
        ///
        /// So the room is forgotten only when leaving was a decision. After a drop the code is kept
        /// and OnDisconnected, which runs next, opens the grace window and starts asking for the
        /// seat back.
        /// </summary>
        public override void OnLeftRoom()
        {
            // Read before it is reset, because it is the only thing here that knows which of the
            // two cases this is.
            bool deliberate = leavingDeliberately;

            // Reset here rather than only in OnDisconnected, because Leave() drops the room without
            // dropping the connection — so the callback that would have reset the flag never runs,
            // and a stale true would silently swallow the next genuine disconnect.
            leavingDeliberately = false;

            if (deliberate)
            {
                // No room to go back to, and nobody holding a seat for us. Without this, a
                // connection dropping later — from the menu, with no game in progress — would find
                // a stale code here and spend five minutes trying to rejoin a match that ended long
                // ago.
                rejoinCode = null;
            }

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

            // A relaunch's seat retry was waiting on this connection, and it is not coming now.
            seatRetryAt = 0f;

            // A seat we can still get back to outranks everything below, and is checked first.
            //
            // Two things used to go wrong here, and together they made a dropped connection end the
            // match on the device that dropped while the other one sat holding its seat for five
            // minutes — the same event, two completely different outcomes.
            //
            // The first was that the grace period was started inside TryRejoin, after
            // ReconnectAndRejoin had agreed to run. When the network is properly gone rather than
            // merely flaky — a cable pulled, wifi switched off — PUN has nothing cached to resume
            // and refuses immediately, so no window ever opened and the match was ended on the
            // spot. But whether we can start an attempt says nothing about whether the seat is
            // there: the server holds it for RejoinGraceSeconds because of PlayerTtl, and it does
            // that regardless of what this client manages to do about it. So the window is opened
            // here, unconditionally, and getting back into it is a separate question answered
            // repeatedly below.
            //
            // The second was that the attempt was made once. A connection that has just gone is the
            // least likely moment for a reconnect to succeed, so the one attempt this made was
            // close to guaranteed to fail — and nothing ever tried again. Coming back online did
            // nothing at all, because by then there was nothing left watching. Update now retries
            // for as long as the window is open.
            if (!string.IsNullOrEmpty(rejoinCode))
            {
                // Whatever attempt was in flight is what just failed. Cleared so the retry in
                // Update is not held off by a flag describing an attempt that is already over.
                rejoining = false;
                intent = Intent.None;

                // We were holding the opponent's seat when our own connection went. Ours is now
                // the one that matters: nothing can happen for them until we are back, and a window
                // left marked as theirs is one Update never reconnects through — the device would
                // sit offline counting down somebody else's clock. Reopened as ours, from now,
                // because now is when the server started holding our seat.
                if (Waiting && !graceIsLocal) ClearGrace();

                if (!Waiting)
                {
                    BeginGrace(local: true);

                    // Next frame, not five seconds from now: the first attempt should be immediate,
                    // and routing it through the same scheduler as every later one keeps a single
                    // path to debug rather than a special case for the first.
                    nextRejoinAttemptAt = Time.realtimeSinceStartup;
                }

                // Otherwise this is an attempt inside the window failing, and the time Update set
                // for the next one stands. Resetting it here made a spin: with the network down an
                // attempt fails within a frame, so "try again now" meant every frame.
                return;
            }

            if (intent != Intent.None)
            {
                // Never got as far as a room, so this is a failed attempt to start rather than a
                // match that ended.
                intent = Intent.None;
                ConnectionFailed?.Invoke();
                return;
            }

            if (Waiting && graceIsLocal) return;

            RaiseMatchEnded(MatchEndReason.LocalDisconnected);
        }

        /// <summary>
        /// Makes one attempt to reconnect and take our seat back.
        ///
        /// <c>ReconnectAndRejoin</c> is tried first rather than a fresh connect-and-join: it reuses
        /// the cached server address and the room we were in, and it is the path that presents us
        /// as the returning player rather than as a new one — which matters because the room
        /// already holds two seats and would refuse a third. When PUN has nothing cached to resume,
        /// the seat is asked for by name instead; see below.
        /// </summary>
        private void TryRejoin()
        {
            // Fast path: PUN still holds the connection it was using, so it can resume that socket
            // and walk straight back into the room. Only from fully disconnected — PUN refuses it
            // in any other state, with a console warning every time it is asked.
            if (!PhotonNetwork.IsConnected && PhotonNetwork.ReconnectAndRejoin())
            {
                rejoining = true;
                return;
            }

            // Slow path, and the one that matters for a real outage. ReconnectAndRejoin refuses
            // when there is nothing cached to resume, which is exactly what a network that was
            // properly down produces — and treating that refusal as the end of the match was the
            // bug. There is still a seat on the server and we still know its room and our own
            // UserId, so the seat can be asked for by name instead.
            //
            // This is the same request a relaunched app makes, and the server answers it the same
            // way for the same reason: the UserId in PlayerPrefs outlives both the socket and the
            // process, so we arrive as the player already in that seat rather than as a third one.
            //
            // Deliberately not routed through the public RejoinRoom, which begins a fresh attempt
            // and would clear the very rejoinCode and grace window this depends on.
            rejoining = true;
            requestedCode = rejoinCode;
            intent = Intent.Rejoin;
            ConnectThenAct();
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
            nextRejoinAttemptAt = 0f;
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
            // A saved match's seat the server was not ready to hand back yet, asked for again now
            // that it has had a moment to notice our old connection is gone.
            if (seatRetryAt > 0f && Time.realtimeSinceStartup >= seatRetryAt)
            {
                seatRetryAt = 0f;
                ConnectThenAct();
            }

            if (!Waiting) return;

            // Keep trying for as long as the seat is ours. Only for our own drop: a window opened
            // for the opponent is theirs to come back through, and there is nothing for this device
            // to reconnect to.
            bool attemptStalled = rejoining &&
                Time.realtimeSinceStartup - rejoinAttemptStartedAt > RejoinAttemptTimeoutSeconds;

            if (graceIsLocal && (!rejoining || attemptStalled)
                && Time.realtimeSinceStartup >= nextRejoinAttemptAt
                && !string.IsNullOrEmpty(rejoinCode) && !PhotonNetwork.InRoom)
            {
                nextRejoinAttemptAt = Time.realtimeSinceStartup + RejoinRetrySeconds;
                rejoinAttemptStartedAt = Time.realtimeSinceStartup;
                TryRejoin();
            }

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
