using System;
using System.Text;

namespace NsoloGame.Net
{
    /// <summary>
    /// The six characters a player reads out to a friend.
    ///
    /// Collision handling deliberately does not live here. The reliable check is to try to create
    /// the room and see whether the server refuses — see PhotonMatchTransport, which regenerates and
    /// retries on "that name is taken". Asking the lobby for a room list first would be both slower
    /// and weaker: it only covers rooms that are visible, open, and in the default lobby, and two
    /// clients can still pass the check simultaneously and then collide.
    /// </summary>
    public static class RoomCode
    {
        public const int Length = 6;

        /// <summary>
        /// Deliberately missing I, L, O, 0 and 1. Codes get read aloud across a room and typed by
        /// people who did not choose them, and those five are where that goes wrong. Dropping them
        /// costs about 12% of the keyspace and still leaves ~887 million combinations.
        /// </summary>
        private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        // Seeded from a Guid rather than the clock: two players tapping "Create Room" in the same
        // millisecond would otherwise be handed the same sequence.
        [ThreadStatic] private static Random random;

        private static Random Rng => random ??= new Random(Guid.NewGuid().GetHashCode());

        public static string Generate()
        {
            var builder = new StringBuilder(Length);
            for (int i = 0; i < Length; i++)
                builder.Append(Alphabet[Rng.Next(Alphabet.Length)]);

            return builder.ToString();
        }

        /// <summary>
        /// Whether a typed code could possibly name a room. This is a shape check, not an existence
        /// check — it is what greys out the Join button, and a code that passes can still turn out
        /// not to exist, which is what the Room Not Found modal is for.
        /// </summary>
        public static bool IsWellFormed(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != Length) return false;

            foreach (char c in code)
                if (Alphabet.IndexOf(char.ToUpperInvariant(c)) < 0) return false;

            return true;
        }

        /// <summary>
        /// Tidies what the player typed: upper-cases it and drops the separators people add when
        /// reading a code back ("abc-def", "ABC DEF").
        ///
        /// It deliberately does not try to guess at the five excluded characters. Folding O onto Q
        /// or I onto J would rewrite the player's input behind their back to a code we have no real
        /// reason to think they meant — and when the guess is wrong they get "Room not found" for a
        /// code they never typed. Leaving the character in place fails the check below instead,
        /// which keeps Join greyed out while the field still shows exactly what they entered.
        /// </summary>
        public static string Normalize(string typed)
        {
            if (string.IsNullOrEmpty(typed)) return string.Empty;

            var builder = new StringBuilder(Length);
            foreach (char raw in typed)
            {
                if (char.IsWhiteSpace(raw) || raw == '-' || raw == '_') continue;
                builder.Append(char.ToUpperInvariant(raw));
            }

            return builder.ToString();
        }
    }
}
