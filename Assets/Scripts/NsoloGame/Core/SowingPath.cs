namespace NsoloGame.Core
{
    /// <summary>
    /// Player-specific counterclockwise sowing paths.
    /// Each player sows only around their own two rows, wrapping over 24 holes.
    /// </summary>
    public class SowingPath
    {
        private const int PathLength = 16;
        private readonly (int r, int c)[] player1Path;
        private readonly (int r, int c)[] player2Path;

        public int Length => PathLength;

        public SowingPath()
        {
            player1Path = new (int, int)[PathLength];
            player2Path = new (int, int)[PathLength];

            int index = 0;
            for (int c = 0; c < 8; c++)
            {
                player1Path[index++] = (0, c);
            }

            for (int c = 7; c >= 0; c--)
            {
                player1Path[index++] = (1, c);
            }

            index = 0;
            for (int c = 0; c < 8; c++)
            {
                player2Path[index++] = (2, c);
            }

            for (int c = 7; c >= 0; c--)
            {
                player2Path[index++] = (3, c);
            }
        }

        public int GetIndex(int player, int r, int c)
        {
            (int r, int c)[] path = GetPathForPlayer(player);
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i].r == r && path[i].c == c)
                {
                    return i;
                }
            }

            return -1;
        }

        public (int r, int c) GetHole(int player, int index)
        {
            (int r, int c)[] path = GetPathForPlayer(player);
            int wrappedIndex = ((index % PathLength) + PathLength) % PathLength;
            return path[wrappedIndex];
        }

        public int Next(int index)
        {
            return (index + 1) % PathLength;
        }

        public (int r, int c)[] GetPath(int player)
        {
            return GetPathForPlayer(player);
        }

        private (int r, int c)[] GetPathForPlayer(int player)
        {
            return player == 1 ? player1Path : player2Path;
        }
    }
}
