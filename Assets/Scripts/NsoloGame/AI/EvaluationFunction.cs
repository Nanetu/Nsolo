namespace NsoloGame.AI
{
    /// <summary>
    /// Evaluation function for Nsolo game board positions.
    /// Weighted sum of multiple heuristics.
    /// </summary>
    public class EvaluationFunction
    {
        // Configurable weights for different evaluation components
        public float WeightStoneDiff { get; set; } = 0.55f;
        public float WeightMobility { get; set; } = 0.20f;
        public float WeightCapThreat { get; set; } = 0.15f;
        public float WeightRelay { get; set; } = 0.10f;

        private Core.GameEngine gameEngine;

        public EvaluationFunction(Core.GameEngine gameEngine)
        {
            this.gameEngine = gameEngine;
        }

        /// <summary>
        /// Evaluate a board position for the given player.
        /// Returns a score where positive = player advantage, negative = opponent advantage.
        /// </summary>
        public float Evaluate(Core.GameBoard board, int player)
        {
            float score = 0f;

            // 1. Stone difference (own stones - opponent stones). Captured stones are folded
            // straight into a player's own pit total (see GameEngine's capture rule), so this
            // single signal already captures both raw stone count and capture advantage.
            int playerStones = gameEngine.GetPlayerStones(board, player);
            int opponentStones = gameEngine.GetPlayerStones(board, 3 - player);
            float stoneDiff = playerStones - opponentStones;
            score += WeightStoneDiff * stoneDiff;

            // 2. Mobility (number of legal moves available, i.e. own pits with >= 2 stones)
            int mobility = GetMobility(board, player);
            score += WeightMobility * mobility;

            // 3. Capture threat score (potential captures available)
            float capThreat = GetCaptureThreatScore(board, player);
            score += WeightCapThreat * capThreat;

            // 4. Relay/board-density potential
            float relayPotential = GetRelayPotential(board, player);
            score += WeightRelay * relayPotential;

            return score;
        }

        /// <summary>
        /// Count playable holes (>= 2 stones, the legal-move threshold) across both of the player's rows.
        /// </summary>
        private int GetMobility(Core.GameBoard board, int player)
        {
            int[] rows = player == 1 ? new[] { 0, 1 } : new[] { 2, 3 };
            int count = 0;

            foreach (int r in rows)
            {
                for (int c = 0; c < Core.GameBoard.Cols; c++)
                {
                    if (board.Get(r, c) >= 2)
                        count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Score based on potential captures: a capture requires landing on a non-empty inner-row pit
        /// while BOTH of the opponent's same-column pits (inner and outer) are occupied. Only count
        /// the threat when the player's own inner pit at that column is occupied (a legal landing spot).
        /// </summary>
        private float GetCaptureThreatScore(Core.GameBoard board, int player)
        {
            float threat = 0f;
            int playerInnerRow = player == 1 ? 1 : 2;
            int opponentInnerRow = player == 1 ? 2 : 1;
            int opponentOuterRow = player == 1 ? 3 : 0;

            for (int c = 0; c < Core.GameBoard.Cols; c++)
            {
                if (board.Get(playerInnerRow, c) < 1)
                    continue;

                int opponentInnerStones = board.Get(opponentInnerRow, c);
                int opponentOuterStones = board.Get(opponentOuterRow, c);

                if (opponentInnerStones >= 1 && opponentOuterStones >= 1)
                {
                    int opponentStones = opponentInnerStones + opponentOuterStones;
                    threat += opponentStones * 0.1f;
                }
            }

            return threat;
        }

        /// <summary>
        /// Density proxy for relay/capture chain potential: more occupied pits on the player's own
        /// side means a sown stone is more likely to land on a non-empty pit and chain via relay or
        /// capture instead of immediately ending the turn (Rule 1).
        /// </summary>
        private float GetRelayPotential(Core.GameBoard board, int player)
        {
            float potential = 0f;
            int[] playerRows = player == 1 ? new[] { 0, 1 } : new[] { 2, 3 };

            foreach (int r in playerRows)
            {
                for (int c = 0; c < Core.GameBoard.Cols; c++)
                {
                    if (board.Get(r, c) >= 1)
                        potential += 0.05f;
                }
            }

            return potential;
        }
    }
}
