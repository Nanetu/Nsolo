using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace NsoloGame.AI
{
    /// <summary>
    /// AI difficulty settings.
    /// </summary>
    public enum Difficulty
    {
        Easy,
        Medium,
        Hard
    }

    /// <summary>
    /// AI agent that uses iterative deepening minimax with alpha-beta pruning.
    /// </summary>
    public class AIAgent
    {
        private Core.GameEngine gameEngine;
        private EvaluationFunction evaluationFunction;
        private TranspositionTable transpositionTable;

        private int maxDepth;
        private float randomMoveProbability;
        private long timeBudgetMs;
        private Difficulty difficulty;

        private Random random = new Random();

        public AIAgent(Core.GameEngine engine, EvaluationFunction eval, Difficulty difficulty)
        {
            this.gameEngine = engine;
            this.evaluationFunction = eval;
            this.transpositionTable = new TranspositionTable();
            this.difficulty = difficulty;

            SetDifficulty(difficulty);
        }

        private void SetDifficulty(Difficulty difficulty)
        {
            switch (difficulty)
            {
                case Difficulty.Easy:
                    maxDepth = 2;
                    randomMoveProbability = 0.3f;
                    timeBudgetMs = 5000;
                    break;
                case Difficulty.Medium:
                    maxDepth = 4;
                    randomMoveProbability = 0.0f;
                    timeBudgetMs = 5000;
                    break;
                case Difficulty.Hard:
                    maxDepth = 6;
                    randomMoveProbability = 0.0f;
                    timeBudgetMs = 5000;
                    break;
            }
        }

        /// <summary>
        /// Select the best move for the AI using iterative deepening minimax.
        /// </summary>
        public Core.Move SelectMove(Core.GameBoard board, int aiPlayer)
        {
            return SelectMove(board, aiPlayer, CancellationToken.None);
        }

        public Core.Move SelectMove(Core.GameBoard board, int aiPlayer, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            // Easy mode: random move selection
            if (difficulty == Difficulty.Easy && (float)random.NextDouble() < randomMoveProbability)
            {
                List<Core.Move> legalMoves = gameEngine.GetLegalMoves(board, aiPlayer);
                if (legalMoves.Count > 0)
                {
                    return legalMoves[random.Next(legalMoves.Count)];
                }
            }

            Stopwatch timer = Stopwatch.StartNew();
            transpositionTable.Clear();

            Core.Move bestMove = null;
            List<Core.Move> legalMoves2 = gameEngine.GetLegalMoves(board, aiPlayer);

            if (legalMoves2.Count == 0)
                return null;

            // Iterative deepening
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (cancellationToken.IsCancellationRequested || timer.ElapsedMilliseconds > timeBudgetMs)
                    break;

                float bestScore = float.MinValue;
                Core.Move candidateMove = null;

                foreach (var move in legalMoves2)
                {
                    if (cancellationToken.IsCancellationRequested || timer.ElapsedMilliseconds > timeBudgetMs)
                        break;

                    Core.GameBoard nextBoard = gameEngine.ApplyMove(board, move, aiPlayer);
                    float score = Minimax(nextBoard, depth - 1, float.MinValue, float.MaxValue, false, aiPlayer, timer.ElapsedTicks, cancellationToken);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        candidateMove = move;
                    }
                }

                if (candidateMove != null)
                {
                    bestMove = candidateMove;
                }
            }

            if (cancellationToken.IsCancellationRequested)
                return null;

            return bestMove ?? legalMoves2[0];
        }

        /// <summary>
        /// Minimax with alpha-beta pruning and transposition table lookup.
        /// </summary>
        private float Minimax(Core.GameBoard board, int depth, float alpha, float beta, bool isMaximising, int aiPlayer, long startTimeTicks, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return 0f;

            long boardHash = board.HashCode();

            // Transposition table lookup
            if (transpositionTable.TryLookup(boardHash, out TranspositionEntry entry))
            {
                if (entry.Depth >= depth)
                {
                    if (entry.Flag == 0) // EXACT
                        return entry.Score;
                    if (entry.Flag == 1) // LOWER
                        alpha = Math.Max(alpha, entry.Score);
                    if (entry.Flag == 2) // UPPER
                        beta = Math.Min(beta, entry.Score);

                    if (alpha >= beta)
                        return entry.Score;
                }
            }

            // Terminal node or max depth reached
            if (depth == 0)
            {
                float score = evaluationFunction.Evaluate(board, aiPlayer);
                transpositionTable.Store(boardHash, new TranspositionEntry(score, depth, 0));
                return score;
            }

            int currentPlayer = board.CurrentPlayer;
            (bool isOver, int winner) = gameEngine.IsTerminal(board, currentPlayer);
            if (isOver)
            {
                float terminalScore = winner == aiPlayer ? float.MaxValue : float.MinValue;
                transpositionTable.Store(boardHash, new TranspositionEntry(terminalScore, depth, 0));
                return terminalScore;
            }

            List<Core.Move> legalMoves = gameEngine.GetLegalMoves(board, currentPlayer);
            if (legalMoves.Count == 0)
            {
                float terminalScore = 3 - currentPlayer == aiPlayer ? float.MaxValue : float.MinValue;
                transpositionTable.Store(boardHash, new TranspositionEntry(terminalScore, depth, 0));
                return terminalScore;
            }

            // Move ordering: captures first (by captured stones), then by stone count descending
            legalMoves = OrderMoves(board, legalMoves, currentPlayer);

            float origAlpha = alpha;
            float value;

            if (isMaximising)
            {
                value = float.MinValue;
                foreach (var move in legalMoves)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Core.GameBoard nextBoard = gameEngine.ApplyMove(board, move, currentPlayer);
                    float score = Minimax(nextBoard, depth - 1, alpha, beta, board.CurrentPlayer != currentPlayer, aiPlayer, startTimeTicks, cancellationToken);
                    value = Math.Max(value, score);
                    alpha = Math.Max(alpha, value);

                    if (beta <= alpha)
                        break;
                }
            }
            else
            {
                value = float.MaxValue;
                foreach (var move in legalMoves)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Core.GameBoard nextBoard = gameEngine.ApplyMove(board, move, currentPlayer);
                    float score = Minimax(nextBoard, depth - 1, alpha, beta, board.CurrentPlayer != currentPlayer, aiPlayer, startTimeTicks, cancellationToken);
                    value = Math.Min(value, score);
                    beta = Math.Min(beta, value);

                    if (beta <= alpha)
                        break;
                }
            }

            // Store in transposition table
            int flag = 0;
            if (value <= origAlpha)
                flag = 2; // UPPER
            else if (value >= beta)
                flag = 1; // LOWER
            else
                flag = 0; // EXACT

            transpositionTable.Store(boardHash, new TranspositionEntry(value, depth, flag));
            return value;
        }

        /// <summary>
        /// Order moves to improve alpha-beta pruning.
        /// Captures first, then by descending stone count.
        /// </summary>
        private List<Core.Move> OrderMoves(Core.GameBoard board, List<Core.Move> moves, int player)
        {
            // Simple heuristic: sort by descending stone count
            return moves.OrderByDescending(m => board.Get(m.Row, m.Col)).ToList();
        }
    }
}
