namespace NsoloGame.Core
{
    /// <summary>
    /// Represents the Nsolo game board state.
    /// 4 rows x 8 columns = 32 holes.
    /// Player 1: rows 0 (outer), 1 (inner)
    /// Player 2: rows 2 (inner), 3 (outer)
    /// </summary>
    public class GameBoard
    {
        public const int Rows = 4;
        public const int Cols = 8;
        public const int HoleCount = Rows * Cols;

        public int[] Board { get; private set; }
        public int CurrentPlayer { get; set; }

        private static long[] zobristTable;

        /// <summary>
        /// Folded in when player 2 is to move, so two positions with the same stones but opposite
        /// sides to move hash differently. Without it they share a transposition entry and read back
        /// each other's scores — a stored score is only meaningful for the side that was on move
        /// when it was computed.
        /// </summary>
        private static long zobristPlayer2ToMove;

        /// <summary>
        /// Initialize Zobrist hashing table (call once at app startup).
        /// </summary>
        public static void InitializeZobrist()
        {
            if (zobristTable != null)
                return;

            zobristTable = new long[HoleCount * (HoleCount + 1)]; // possible stone counts 0..HoleCount

            System.Random random = new System.Random(42);
            for (int i = 0; i < zobristTable.Length; i++)
            {
                zobristTable[i] = ((long)random.Next() << 32) | (uint)random.Next();
            }

            // Drawn after the table so the pit keys keep the values they had before the
            // side-to-move key existed, and the seeded sequence stays reproducible.
            zobristPlayer2ToMove = ((long)random.Next() << 32) | (uint)random.Next();
        }

        /// <summary>
        /// Initialize game board with starting position.
        /// Every pit starts with 2 stones (32 per player).
        /// </summary>
        public GameBoard()
        {
            Board = new int[HoleCount];
            for (int i = 0; i < HoleCount; i++)
            {
                Board[i] = 2;
            }

            CurrentPlayer = 1;
        }

        public int Get(int r, int c)
        {
            return Board[r * Cols + c];
        }

        public void Set(int r, int c, int value)
        {
            Board[r * Cols + c] = value;
        }

        public GameBoard Clone()
        {
            GameBoard clone = new GameBoard();
            System.Array.Copy(Board, clone.Board, HoleCount);
            clone.CurrentPlayer = CurrentPlayer;
            return clone;
        }

        public long HashCode()
        {
            if (zobristTable == null)
                InitializeZobrist();

            long hash = 0;
            for (int i = 0; i < HoleCount; i++)
            {
                int stoneCount = Board[i];
                if (stoneCount > HoleCount)
                    stoneCount = HoleCount;
                hash ^= zobristTable[i * (HoleCount + 1) + stoneCount];
            }

            if (CurrentPlayer == 2)
                hash ^= zobristPlayer2ToMove;

            return hash;
        }

        public override string ToString()
        {
            return $"GameBoard[CurrentPlayer={CurrentPlayer}]";
        }
    }
}
