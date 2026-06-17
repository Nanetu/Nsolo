using System.Collections.Generic;

namespace NsoloGame.Core
{
    /// <summary>
    /// Stateless game engine for Nsolo. Handles move generation and application.
    /// </summary>
    public class GameEngine
    {
        private SowingPath sowingPath;

        public GameEngine(SowingPath sowingPath)
        {
            this.sowingPath = sowingPath;
        }

        /// <summary>
        /// Get all legal moves for a player.
        /// A legal move is picking up stones from any hole in the player's rows with >= 1 stone.
        /// Player 1: rows 0, 1
        /// Player 2: rows 2, 3
        /// </summary>
        public List<Move> GetLegalMoves(GameBoard board, int player)
        {
            List<Move> moves = new List<Move>();
            int[] rows = player == 1 ? new[] { 0, 1 } : new[] { 2, 3 };

            foreach (int r in rows)
            {
                for (int c = 0; c < 12; c++)
                {
                    if (board.Get(r, c) >= 1)
                    {
                        int pathIndex = sowingPath.GetIndex(player, r, c);
                        moves.Add(new Move(r, c, pathIndex));
                    }
                }
            }

            return moves;
        }

        /// <summary>
        /// Apply a move to the board and return the updated board.
        /// Handles sowing, relay capture (multi-lap), and capture evaluation.
        /// </summary>
        public GameBoard ApplyMove(GameBoard board, Move move, int player)
        {
            return ApplyMoveWithResult(board, move, player).Board;
        }

        public MoveResult ApplyMoveWithResult(GameBoard board, Move move, int player)
        {
            GameBoard newBoard = board.Clone();
            List<(int r, int c)> landingSequence = new List<(int r, int c)>();
            List<SowingSegment> sowingSegments = new List<SowingSegment>();

            // Step 1: Pick up stones
            int r = move.Row;
            int c = move.Col;
            int n = newBoard.Get(r, c);
            newBoard.Set(r, c, 0);

            // Step 2: Sow stones
            int currentIndex = move.PathIndex;
            currentIndex = SowSegment(newBoard, player, currentIndex, n, (r, c), landingSequence, sowingSegments);

            // Step 3: Relay capture (multi-lap sowing)
            while (newBoard.Get(sowingPath.GetHole(player, currentIndex).r, sowingPath.GetHole(player, currentIndex).c) > 1)
            {
                var (lastR, lastC) = sowingPath.GetHole(player, currentIndex);
                int stones = newBoard.Get(lastR, lastC);
                newBoard.Set(lastR, lastC, 0);

                currentIndex = SowSegment(newBoard, player, currentIndex, stones, (lastR, lastC), landingSequence, sowingSegments);
            }

            // Step 4: Evaluate capture
            int capturedStones = EvaluateCapture(newBoard, currentIndex, player);

            // Step 5: Switch player
            newBoard.CurrentPlayer = player == 1 ? 2 : 1;

            return new MoveResult(
                newBoard,
                move,
                player,
                landingSequence,
                sowingSegments,
                sowingPath.GetHole(player, currentIndex),
                capturedStones);
        }

        private int SowSegment(
            GameBoard board,
            int player,
            int currentIndex,
            int stones,
            (int r, int c) source,
            List<(int r, int c)> landingSequence,
            List<SowingSegment> sowingSegments)
        {
            List<(int r, int c)> segmentLandings = new List<(int r, int c)>();

            while (stones > 0)
            {
                currentIndex = sowingPath.Next(currentIndex);
                var (sr, sc) = sowingPath.GetHole(player, currentIndex);
                board.Set(sr, sc, board.Get(sr, sc) + 1);
                landingSequence.Add((sr, sc));
                segmentLandings.Add((sr, sc));
                stones--;
            }

            sowingSegments.Add(new SowingSegment(source, segmentLandings));
            return currentIndex;
        }

        /// <summary>
        /// Evaluate and apply captures.
        /// Capture if:
        /// - Final hole is in player's inner row (P1: r==1, P2: r==2)
        /// - Final hole has exactly 1 stone (was empty before sowing)
        /// 
        /// Capture: take opponent's stones at same column in their inner row
        /// Extended: only if the inner row capture above was non-zero, also take
        /// opponent's outer row stones at same column. If the inner row was already
        /// empty, no capture occurs at all (prevents "stealing" outer-row stones
        /// when the inner row has nothing to capture).
        /// </summary>
        private int EvaluateCapture(GameBoard board, int lastIndex, int player)
        {
            var (finalR, finalC) = sowingPath.GetHole(player, lastIndex);
            bool isPlayerInnerRow = (player == 1 && finalR == 1) || (player == 2 && finalR == 2);
            bool isExactlyOne = board.Get(finalR, finalC) == 1;

            if (!isPlayerInnerRow || !isExactlyOne)
                return 0;

            // Primary capture: opponent's inner row at same column
            int opponentInnerRow = player == 1 ? 2 : 1;
            int opponentOuterRow = player == 1 ? 3 : 0;

            int primaryStones = board.Get(opponentInnerRow, finalC);

            if (primaryStones == 0)
                return 0;

            board.Set(opponentInnerRow, finalC, 0);

            // Extended capture: opponent's outer row at same column, only if inner row had stones to capture
            int extendedStones = board.Get(opponentOuterRow, finalC);
            board.Set(opponentOuterRow, finalC, 0);

            int totalCaptured = primaryStones + extendedStones;

            if (player == 1)
            {
                board.CapturedP1 += totalCaptured;
            }
            else
            {
                board.CapturedP2 += totalCaptured;
            }

            return totalCaptured;
        }

        /// <summary>
        /// Check if game is terminal.
        /// Game is over if the current player has no legal moves (all holes empty).
        /// Returns (isOver, winner) where winner is 1 or 2.
        /// </summary>
        public (bool isOver, int winner) IsTerminal(GameBoard board, int player)
        {
            List<Move> legalMoves = GetLegalMoves(board, player);
            if (legalMoves.Count == 0)
            {
                // Current player has no moves, opponent wins
                int winner = player == 1 ? 2 : 1;
                return (true, winner);
            }

            return (false, -1);
        }

        /// <summary>
        /// Get the current player's stone count (all holes in their rows).
        /// </summary>
        public int GetPlayerStones(GameBoard board, int player)
        {
            int[] rows = player == 1 ? new[] { 0, 1 } : new[] { 2, 3 };
            int total = 0;

            foreach (int r in rows)
            {
                for (int c = 0; c < 12; c++)
                {
                    total += board.Get(r, c);
                }
            }

            return total;
        }
    }
}
