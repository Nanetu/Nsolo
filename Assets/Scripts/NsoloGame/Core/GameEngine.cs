using System.Collections.Generic;
using UnityEngine;

namespace NsoloGame.Core
{
    /// <summary>
    /// Stateless game engine for Nsolo. Handles move generation and application.
    /// </summary>
    public class GameEngine
    {
        private const int MaxSowSegments = 10000;

        /// <summary>
        /// When true, logs every landing-outcome decision (capture vs relay vs turn-end) to the
        /// Console. Off by default since the AI's minimax search calls ApplyMoveWithResult many
        /// thousands of times — only enable while debugging a specific human move.
        /// </summary>
        public static bool DebugLandingOutcomes = false;

        private SowingPath sowingPath;

        public GameEngine(SowingPath sowingPath)
        {
            this.sowingPath = sowingPath;
        }

        /// <summary>
        /// Get all legal moves for a player.
        /// A legal move is picking up stones from any hole in the player's rows with >= 2 stones
        /// (a pit with exactly one stone cannot be selected).
        /// Player 1: rows 0, 1. Player 2: rows 2, 3.
        /// </summary>
        public List<Move> GetLegalMoves(GameBoard board, int player)
        {
            List<Move> moves = new List<Move>();
            int[] rows = player == 1 ? new[] { 0, 1 } : new[] { 2, 3 };

            foreach (int r in rows)
            {
                for (int c = 0; c < GameBoard.Cols; c++)
                {
                    if (board.Get(r, c) >= 2)
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
        /// Handles sowing, relay sowing, captures, and the capture-restart rule.
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

            int originalIndex = move.PathIndex;
            int totalCaptured = 0;

            // Step 1: Pick up stones from the selected pit and sow them.
            int r = move.Row;
            int c = move.Col;
            int n = newBoard.Get(r, c);
            newBoard.Set(r, c, 0);

            int currentIndex = SowSegment(newBoard, player, originalIndex, n, (r, c), landingSequence, sowingSegments);

            // Step 2: Resolve the landing outcome, looping through relays/captures
            // until a stone lands in a pit that was empty before it was placed.
            int safety = 0;
            while (true)
            {
                if (++safety > MaxSowSegments)
                {
                    Debug.LogError("GameEngine: exceeded max sow segments — aborting turn to avoid an infinite loop.");
                    break;
                }

                var (landR, landC) = sowingPath.GetHole(player, currentIndex);
                int finalValue = newBoard.Get(landR, landC);
                bool wasEmptyBeforeFinalStone = finalValue - 1 == 0;

                if (wasEmptyBeforeFinalStone)
                {
                    // Rule 1: turn ends immediately. No relay, no capture.
                    if (DebugLandingOutcomes)
                        Debug.Log($"[GameEngine] Landed player={player} ({landR},{landC}) finalValue={finalValue} -> TURN END (pit was empty before final stone)");
                    break;
                }

                bool isPlayerInnerRow = (player == 1 && landR == 1) || (player == 2 && landR == 2);
                bool isPlayerOuterRow = (player == 1 && landR == 0) || (player == 2 && landR == 3);

                if (isPlayerInnerRow)
                {
                    int opponentInnerRow = player == 1 ? 2 : 1;
                    int opponentOuterRow = player == 1 ? 3 : 0;

                    int opponentInnerStones = newBoard.Get(opponentInnerRow, landC);
                    int opponentOuterStones = newBoard.Get(opponentOuterRow, landC);

                    if (DebugLandingOutcomes)
                        Debug.Log($"[GameEngine] Landed player={player} ({landR},{landC}) finalValue={finalValue} | opponentInner({opponentInnerRow},{landC})={opponentInnerStones} opponentOuter({opponentOuterRow},{landC})={opponentOuterStones} -> {(opponentInnerStones >= 1 && opponentOuterStones >= 1 ? "CAPTURE" : "relay")}");

                    if (opponentInnerStones >= 1 && opponentOuterStones >= 1)
                    {
                        // Rule 2 (capture branch): zero out only the opponent's two same-column
                        // pits, then resow those stones starting after the ORIGINAL pit picked
                        // up this turn (capture-restart rule). The player's landing pit keeps
                        // its stones — they are not scooped or resown.
                        int captured = opponentInnerStones + opponentOuterStones;
                        newBoard.Set(opponentInnerRow, landC, 0);
                        newBoard.Set(opponentOuterRow, landC, 0);

                        totalCaptured += captured;

                        var extraSources = new List<(int r, int c)> { (opponentOuterRow, landC) };
                        currentIndex = SowSegment(newBoard, player, originalIndex, captured, (opponentInnerRow, landC), landingSequence, sowingSegments, extraSources);
                        continue;
                    }
                    else
                    {
                        // Rule 2 (relay branch): pick up the landing pit and continue.
                        int stones = finalValue;
                        newBoard.Set(landR, landC, 0);
                        currentIndex = SowSegment(newBoard, player, currentIndex, stones, (landR, landC), landingSequence, sowingSegments);
                        continue;
                    }
                }
                else if (isPlayerOuterRow)
                {
                    // Rule 3: relay sowing unconditionally.
                    if (DebugLandingOutcomes)
                        Debug.Log($"[GameEngine] Landed player={player} ({landR},{landC}) finalValue={finalValue} -> relay (outer row, unconditional)");
                    int stones = finalValue;
                    newBoard.Set(landR, landC, 0);
                    currentIndex = SowSegment(newBoard, player, currentIndex, stones, (landR, landC), landingSequence, sowingSegments);
                    continue;
                }
                else
                {
                    // Should never happen: every hole belongs to either the player's inner or outer row.
                    break;
                }
            }

            // Switch player.
            newBoard.CurrentPlayer = player == 1 ? 2 : 1;

            return new MoveResult(
                newBoard,
                move,
                player,
                landingSequence,
                sowingSegments,
                sowingPath.GetHole(player, currentIndex),
                totalCaptured);
        }

        private int SowSegment(
            GameBoard board,
            int player,
            int currentIndex,
            int stones,
            (int r, int c) source,
            List<(int r, int c)> landingSequence,
            List<SowingSegment> sowingSegments,
            List<(int r, int c)> extraSources = null)
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

            sowingSegments.Add(new SowingSegment(source, segmentLandings, extraSources));
            return currentIndex;
        }

        /// <summary>
        /// Check if game is terminal.
        /// Game is over if the current player has no legal moves
        /// (every pit on their side has 0 or 1 stones).
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
                for (int c = 0; c < GameBoard.Cols; c++)
                {
                    total += board.Get(r, c);
                }
            }

            return total;
        }
    }
}
