using System.Collections.Generic;

namespace NsoloGame.AI
{
    /// <summary>
    /// Entry in the transposition table (hash table of evaluated positions).
    /// </summary>
    public class TranspositionEntry
    {
        public float Score { get; set; }
        public int Depth { get; set; }
        public int Flag { get; set; }  // 0 = EXACT, 1 = LOWER, 2 = UPPER

        public TranspositionEntry(float score, int depth, int flag)
        {
            Score = score;
            Depth = depth;
            Flag = flag;
        }
    }

    /// <summary>
    /// Hash table for storing previously evaluated positions to avoid re-computation.
    /// Supports up to 100,000 entries.
    /// </summary>
    public class TranspositionTable
    {
        private Dictionary<long, TranspositionEntry> table;
        private const int MaxEntries = 100000;

        public TranspositionTable()
        {
            table = new Dictionary<long, TranspositionEntry>(MaxEntries);
        }

        /// <summary>
        /// Try to lookup a board position in the table.
        /// Returns true if found, sets entry if successful.
        /// </summary>
        public bool TryLookup(long boardHash, out TranspositionEntry entry)
        {
            return table.TryGetValue(boardHash, out entry);
        }

        /// <summary>
        /// Store an evaluated position in the table.
        /// </summary>
        public void Store(long boardHash, TranspositionEntry entry)
        {
            // If at max capacity, don't add (simple eviction policy)
            if (table.Count >= MaxEntries && !table.ContainsKey(boardHash))
                return;

            table[boardHash] = entry;
        }

        /// <summary>
        /// Clear all entries from the table.
        /// </summary>
        public void Clear()
        {
            table.Clear();
        }

        public int Count => table.Count;
    }
}
