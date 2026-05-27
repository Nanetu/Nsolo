namespace NsoloGame.Core
{
    /// <summary>
    /// Represents the Nsolo game board state.
    /// 4 rows x 12 columns = 48 holes.
    /// Player 1: rows 0 (outer), 1 (inner)
    /// Player 2: rows 2 (inner), 3 (outer)
    /// </summary>
    public class GameBoard
    {
        public int[] Board { get; private set; }
        public int CapturedP1 { get; set; }
        public int CapturedP2 { get; set; }
        public int CurrentPlayer { get; set; }

        private static long[] zobristTable;

        /// <summary>
        /// Initialize Zobrist hashing table (call once at app startup).
        /// </summary>
        public static void InitializeZobrist()
        {
            if (zobristTable != null)
                return;

            zobristTable = new long[48 * 49]; // 48 holes, 49 possible stone counts (0-48)
            
            System.Random random = new System.Random(42);
            for (int i = 0; i < zobristTable.Length; i++)
            {
                zobristTable[i] = ((long)random.Next() << 32) | (uint)random.Next();
            }
        }

        /// <summary>
        /// Initialize game board with starting position.
        /// All holes = 2 EXCEPT B[1][11] = 0 and B[2][0] = 0
        /// </summary>
        public GameBoard()
        {
            Board = new int[48];
            for (int i = 0; i < 48; i++)
            {
                Board[i] = 2;
            }
            // P1 inner right (r=1, c=11) = 0
            Board[1 * 12 + 11] = 0;
            // P2 inner left (r=2, c=0) = 0
            Board[2 * 12 + 0] = 0;

            CapturedP1 = 0;
            CapturedP2 = 0;
            CurrentPlayer = 1;
        }

        public int Get(int r, int c)
        {
            return Board[r * 12 + c];
        }

        public void Set(int r, int c, int value)
        {
            Board[r * 12 + c] = value;
        }

        public GameBoard Clone()
        {
            GameBoard clone = new GameBoard();
            System.Array.Copy(Board, clone.Board, 48);
            clone.CapturedP1 = CapturedP1;
            clone.CapturedP2 = CapturedP2;
            clone.CurrentPlayer = CurrentPlayer;
            return clone;
        }

        public long HashCode()
        {
            if (zobristTable == null)
                InitializeZobrist();

            long hash = 0;
            for (int i = 0; i < 48; i++)
            {
                int stoneCount = Board[i];
                if (stoneCount > 48)
                    stoneCount = 48;
                hash ^= zobristTable[i * 49 + stoneCount];
            }
            hash ^= (long)CapturedP1 << 32;
            hash ^= CapturedP2;
            return hash;
        }

        public override string ToString()
        {
            return $"GameBoard[CapturedP1={CapturedP1}, CapturedP2={CapturedP2}, CurrentPlayer={CurrentPlayer}]";
        }
    }
}
