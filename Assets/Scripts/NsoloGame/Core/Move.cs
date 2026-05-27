namespace NsoloGame.Core
{
    /// <summary>
    /// Represents a move in Nsolo: picking up stones from a specific hole.
    /// </summary>
    public class Move
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public int PathIndex { get; set; }

        public Move(int row, int col, int pathIndex)
        {
            Row = row;
            Col = col;
            PathIndex = pathIndex;
        }

        public override string ToString()
        {
            return $"Move(r={Row}, c={Col}, pathIdx={PathIndex})";
        }
    }
}
