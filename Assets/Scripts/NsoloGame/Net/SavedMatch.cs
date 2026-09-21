using System;
using UnityEngine;

namespace NsoloGame.Net
{
    /// <summary>
    /// The one thing a device needs to remember in order to walk back into an online game it was
    /// thrown out of: which room, which seat, and until when.
    ///
    /// This exists for the failure the reconnect path cannot cover. A connection that drops while
    /// the app is running is handled entirely inside <see cref="PhotonMatchTransport"/> — PUN still
    /// holds a cached connection, <c>ReconnectAndRejoin</c> takes the seat straight back, and the
    /// player is returned to their board without being asked anything. Nothing here is involved and
    /// nothing should be: a modal asking "would you like to rejoin?" after the rejoin has already
    /// happened is a tap that buys nothing.
    ///
    /// What that path cannot survive is the process going away. Android kills backgrounded apps, and
    /// a killed app comes back with no cached connection to resume, no room, no seat and no board —
    /// while the opponent's device is still holding the seat open for five minutes. Before this, the
    /// only route back was Play Online, Join Room, and typing a six-character code that the player
    /// most likely never saw and certainly did not memorise. So the code is written down here while
    /// the match is live, and the next launch can offer to use it.
    ///
    /// PlayerPrefs rather than a file for the same reason the Photon UserId is kept there
    /// (see <c>PhotonMatchTransport</c>): this is three small values that must survive a process
    /// death, which is exactly what it is for.
    ///
    /// The deadline is stored as an absolute UTC timestamp rather than a duration. The point of the
    /// record is to outlive the process, and every in-engine clock — <c>Time.realtimeSinceStartup</c>
    /// included — restarts from zero when the app does, so a duration measured against one would
    /// come back looking fresh no matter how long the phone had been off.
    /// </summary>
    public static class SavedMatch
    {
        private const string CodeKey = "SavedMatch_Code";
        private const string SeatKey = "SavedMatch_Seat";
        private const string ExpiryKey = "SavedMatch_ExpiresUtc";

        /// <summary>A match worth offering to rejoin: the room, the seat, and how long is left.</summary>
        public readonly struct Record
        {
            public readonly string Code;
            public readonly int Seat;
            public readonly float SecondsLeft;

            public Record(string code, int seat, float secondsLeft)
            {
                Code = code;
                Seat = seat;
                SecondsLeft = secondsLeft;
            }
        }

        /// <summary>
        /// Writes down a match that has just begun in earnest.
        ///
        /// Called when play starts rather than when the room fills. A player who loses the app while
        /// arranging their stones has lost half a minute of tapping and can set it out again in the
        /// next game; one who loses it eight moves into a position they are winning has lost
        /// something worth going back for, and it is only from that point that there is a board to
        /// go back to.
        /// </summary>
        public static void Remember(string code, int seat, float holdSeconds)
        {
            if (string.IsNullOrEmpty(code) || seat is not (1 or 2))
            {
                Debug.LogWarning($"SavedMatch: refusing to remember a match with code '{code}' and seat {seat}.");
                return;
            }

            PlayerPrefs.SetString(CodeKey, code);
            PlayerPrefs.SetInt(SeatKey, seat);
            PlayerPrefs.SetString(ExpiryKey,
                DateTime.UtcNow.AddSeconds(holdSeconds).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            PlayerPrefs.Save();

            Debug.Log($"SavedMatch: holding room {code} (seat {seat}) for {holdSeconds:0}s.");
        }

        /// <summary>
        /// The saved match, or null when there is none worth offering — nothing was saved, it is
        /// malformed, or its window has closed.
        ///
        /// Reading a closed record clears it, so a stale entry cannot sit in PlayerPrefs offering a
        /// room that expired days ago every time the app opens.
        /// </summary>
        public static Record? Load()
        {
            string code = PlayerPrefs.GetString(CodeKey, null);
            if (string.IsNullOrEmpty(code)) return null;

            int seat = PlayerPrefs.GetInt(SeatKey, 0);
            string expiry = PlayerPrefs.GetString(ExpiryKey, null);

            if (seat is not (1 or 2) ||
                !DateTime.TryParse(expiry, System.Globalization.CultureInfo.InvariantCulture,
                                   System.Globalization.DateTimeStyles.RoundtripKind, out DateTime expiresUtc))
            {
                Debug.LogWarning("SavedMatch: the saved record is malformed; discarding it.");
                Forget();
                return null;
            }

            double left = (expiresUtc - DateTime.UtcNow).TotalSeconds;
            if (left <= 0d)
            {
                Forget();
                return null;
            }

            return new Record(code, seat, (float)left);
        }

        /// <summary>Whether there is a live match to offer. Clears an expired one on the way past.</summary>
        public static bool Exists => Load() != null;

        public static void Forget()
        {
            PlayerPrefs.DeleteKey(CodeKey);
            PlayerPrefs.DeleteKey(SeatKey);
            PlayerPrefs.DeleteKey(ExpiryKey);
            PlayerPrefs.Save();
        }
    }
}
