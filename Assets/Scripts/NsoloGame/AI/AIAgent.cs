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
        private readonly Dictionary<int, int> moveHistoryScores = new Dictionary<int, int>();

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
                    maxDepth = 1;
                    randomMoveProbability = 0.9f;
                    timeBudgetMs = 3000;
                    break;
                case Difficulty.Medium:
                    maxDepth = 3;
                    randomMoveProbability = 0.45f;
                    timeBudgetMs = 3000;
                    break;
                case Difficulty.Hard:
                    maxDepth = 6;
                    randomMoveProbability = 0.0f;
                    timeBudgetMs = 3000;
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

            // Easy/Medium mode: occasionally pick a random legal move instead of searching
            if (difficulty != Difficulty.Hard && (float)random.NextDouble() < randomMoveProbability)
            {
                List<Core.Move> legalMoves = gameEngine.GetLegalMoves(board, aiPlayer);
                if (legalMoves.Count > 0)
                {
                    return legalMoves[random.Next(legalMoves.Count)];
                }
            }

            return SearchBestMove(board, aiPlayer, maxDepth, timeBudgetMs, cancellationToken);
        }

        /// <summary>
        /// Iterative-deepening alpha-beta search shared by the AI's move selection and the hint
        /// system. Searches depth 1, 2, 3… keeping the best move from the last <em>fully completed</em>
        /// depth, and bails out as soon as <paramref name="budgetMs"/> or cancellation is hit — so it
        /// always has a usable move ready and stays responsive. Deterministic (no randomness).
        /// </summary>
        private Core.Move SearchBestMove(Core.GameBoard board, int player, int depthCap, long budgetMs, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            Stopwatch timer = Stopwatch.StartNew();
            transpositionTable.Clear();
            moveHistoryScores.Clear();

            List<Core.Move> legalMoves = gameEngine.GetLegalMoves(board, player);
            if (legalMoves.Count == 0)
                return null;

            Core.Move bestMove = null;

            for (int depth = 1; depth <= depthCap; depth++)
            {
                if (cancellationToken.IsCancellationRequested || timer.ElapsedMilliseconds > budgetMs)
                    break;

                float bestScore = float.MinValue;
                Core.Move candidateMove = null;
                bool depthCompleted = true;
                List<Core.Move> orderedRootMoves = OrderMoves(board, legalMoves, player, bestMove);

                foreach (var move in orderedRootMoves)
                {
                    if (cancellationToken.IsCancellationRequested || timer.ElapsedMilliseconds > budgetMs)
                    {
                        depthCompleted = false;
                        break;
                    }

                    Core.MoveResult moveResult = gameEngine.ApplyMoveWithResult(board, move, player);
                    RecordMoveOrderingSignal(move, player, moveResult, depth);
                    float score = Minimax(moveResult.Board, depth - 1, float.MinValue, float.MaxValue, false, player, timer.ElapsedTicks, cancellationToken);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        candidateMove = move;
                    }
                }

                // Only trust a depth's result if it finished; a time-interrupted depth may have
                // only looked at a few root moves. The exception is the very first depth, where a
                // partial answer still beats returning nothing.
                if (candidateMove != null && (depthCompleted || bestMove == null))
                    bestMove = candidateMove;
            }

            if (cancellationToken.IsCancellationRequested)
                return null;

            return bestMove ?? legalMoves[0];
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

            legalMoves = OrderMoves(board, legalMoves, currentPlayer, null);

            float origAlpha = alpha;
            float value;
            int storedFlag = 0;

            if (isMaximising)
            {
                value = float.MinValue;
                foreach (var move in legalMoves)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Core.MoveResult moveResult = gameEngine.ApplyMoveWithResult(board, move, currentPlayer);
                    float score = Minimax(moveResult.Board, depth - 1, alpha, beta, board.CurrentPlayer != currentPlayer, aiPlayer, startTimeTicks, cancellationToken);
                    value = Math.Max(value, score);
                    alpha = Math.Max(alpha, value);

                    if (beta <= alpha)
                    {
                        RecordMoveOrderingSignal(move, currentPlayer, moveResult, depth);
                        storedFlag = 1;
                        break;
                    }
                }
            }
            else
            {
                value = float.MaxValue;
                foreach (var move in legalMoves)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Core.MoveResult moveResult = gameEngine.ApplyMoveWithResult(board, move, currentPlayer);
                    float score = Minimax(moveResult.Board, depth - 1, alpha, beta, board.CurrentPlayer != currentPlayer, aiPlayer, startTimeTicks, cancellationToken);
                    value = Math.Min(value, score);
                    beta = Math.Min(beta, value);

                    if (beta <= alpha)
                    {
                        RecordMoveOrderingSignal(move, currentPlayer, moveResult, depth);
                        storedFlag = 2;
                        break;
                    }
                }
            }

            // Store in transposition table
            int flag = storedFlag;
            if (flag == 0)
            {
                if (value <= origAlpha)
                    flag = 2; // UPPER
                else if (value >= beta)
                    flag = 1; // LOWER
                else
                    flag = 0; // EXACT
            }

            transpositionTable.Store(boardHash, new TranspositionEntry(value, depth, flag));
            return value;
        }

        /// <summary>
        /// Order moves to improve alpha-beta pruning.
        /// Previous-depth best moves are searched first, followed by capture and relay moves,
        /// then moves that caused prior cutoffs, then heavier pits.
        /// </summary>
        private List<Core.Move> OrderMoves(Core.GameBoard board, List<Core.Move> moves, int player, Core.Move preferredMove)
        {
            return moves
                .Select(move => CreateMoveOrderingInfo(board, move, player, preferredMove))
                .OrderByDescending(info => info.Score)
                .Select(info => info.Move)
                .ToList();
        }

        private MoveOrderingInfo CreateMoveOrderingInfo(Core.GameBoard board, Core.Move move, int player, Core.Move preferredMove)
        {
            int score = 0;
            if (IsSameMove(move, preferredMove))
            {
                score += 100000;
            }

            Core.MoveResult result = gameEngine.ApplyMoveWithResult(board, move, player);
            score += result.CapturedStones * 1000;
            score += Math.Max(0, result.SowingSegments.Count - 1) * 120;
            score += result.LandingSequence.Count * 5;
            score += board.Get(move.Row, move.Col);

            if (moveHistoryScores.TryGetValue(GetMoveKey(move, player), out int historyScore))
            {
                score += historyScore;
            }

            return new MoveOrderingInfo(move, score);
        }

        private void RecordMoveOrderingSignal(Core.Move move, int player, Core.MoveResult result, int depth)
        {
            int key = GetMoveKey(move, player);
            int score = depth * depth;

            if (result != null)
            {
                score += result.CapturedStones * 10;
                score += Math.Max(0, result.SowingSegments.Count - 1) * 4;
            }

            moveHistoryScores.TryGetValue(key, out int existingScore);
            moveHistoryScores[key] = existingScore + score;
        }

        private int GetMoveKey(Core.Move move, int player)
        {
            return player * 1000 + move.Row * 8 + move.Col;
        }

        // Hint search tuning. The hint should be a strong move but arrive quickly, so it uses the
        // same iterative-deepening search as the AI with a high depth cap but a short time budget —
        // the budget, not the depth, is what actually bounds how long the player waits.
        private const int HintDepthCap = 6;
        private const long HintTimeBudgetMs = 700;

        /// <summary>
        /// Best-move suggestion for the hint system. Runs the same deterministic iterative-deepening
        /// alpha-beta search the AI uses, from <paramref name="player"/>'s perspective, but capped by
        /// a short time budget so it returns a good move promptly and cancels instantly. Independent
        /// of the current difficulty, so hints are always strong even on Easy.
        /// </summary>
        public Core.Move GetHintMove(Core.GameBoard board, int player, CancellationToken ct)
        {
            return SearchBestMove(board, player, HintDepthCap, HintTimeBudgetMs, ct);
        }

        private bool IsSameMove(Core.Move left, Core.Move right)
        {
            if (left == null || right == null)
                return false;

            return left.Row == right.Row && left.Col == right.Col;
        }

        private class MoveOrderingInfo
        {
            public Core.Move Move { get; }
            public int Score { get; }

            public MoveOrderingInfo(Core.Move move, int score)
            {
                Move = move;
                Score = score;
            }
        }
    }
}
