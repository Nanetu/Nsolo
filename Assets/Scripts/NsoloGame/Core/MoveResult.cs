using System.Collections.Generic;

namespace NsoloGame.Core
{
    public class SowingSegment
    {
        public (int r, int c) Source { get; }
        public List<(int r, int c)> Landings { get; }

        /// <summary>
        /// Additional pits whose stones are scooped into this same segment alongside <see cref="Source"/>.
        /// Used for captures, where stones are gathered from the landing pit and both of the opponent's
        /// same-column pits before being resown as a single group.
        /// </summary>
        public List<(int r, int c)> ExtraSources { get; }

        public SowingSegment((int r, int c) source, List<(int r, int c)> landings, List<(int r, int c)> extraSources = null)
        {
            Source = source;
            Landings = landings;
            ExtraSources = extraSources ?? new List<(int r, int c)>();
        }
    }

    /// <summary>
    /// Result details for one fully resolved move, including relay and capture.
    /// </summary>
    public class MoveResult
    {
        public GameBoard Board { get; }
        public Move Move { get; }
        public int Player { get; }
        public List<(int r, int c)> LandingSequence { get; }
        public List<SowingSegment> SowingSegments { get; }
        public (int r, int c) FinalHole { get; }
        public int CapturedStones { get; }

        public MoveResult(
            GameBoard board,
            Move move,
            int player,
            List<(int r, int c)> landingSequence,
            List<SowingSegment> sowingSegments,
            (int r, int c) finalHole,
            int capturedStones)
        {
            Board = board;
            Move = move;
            Player = player;
            LandingSequence = landingSequence;
            SowingSegments = sowingSegments;
            FinalHole = finalHole;
            CapturedStones = capturedStones;
        }
    }
}
