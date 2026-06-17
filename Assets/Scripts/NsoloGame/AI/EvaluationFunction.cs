namespace NsoloGame.AI
{
    /// <summary>
    /// Evaluation function for Nsolo game board positions.
    /// Weighted sum of multiple heuristics.
    /// </summary>
    public class EvaluationFunction
    {
        // Configurable weights for different evaluation components
        public float WeightStoneDiff { get; set; } = 0.15f;
        public float WeightCapDiff { get; set; } = 0.40f;
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

            // 1. Stone difference (own stones - opponent stones)
            int playerStones = gameEngine.GetPlayerStones(board, player);
            int opponentStones = gameEngine.GetPlayerStones(board, 3 - player);
            float stoneDiff = playerStones - opponentStones;
            score += WeightStoneDiff * stoneDiff;

            // 2. Capture difference (captured by player - captured by opponent)
            int playerCaptured = player == 1 ? board.CapturedP1 : board.CapturedP2;
            int opponentCaptured = player == 1 ? board.CapturedP2 : board.CapturedP1;
            float capDiff = playerCaptured - opponentCaptured;
            score += WeightCapDiff * capDiff;

            // 3. Inner row mobility (number of playable holes in inner row)
            int innerRowMobility = GetInnerRowMobility(board, player);
            score += WeightMobility * innerRowMobility;

            // 4. Capture threat score (potential captures available)
            float capThreat = GetCaptureThreatScore(board, player);
            score += WeightCapThreat * capThreat;

            // 5. Relay potential (holes with many stones)
            float relayPotential = GetRelayPotential(board, player);
            score += WeightRelay * relayPotential;

            return score;
        }

        /// <summary>
        /// Count playable holes (>= 1 stone) in player's inner row.
        /// </summary>
        private int GetInnerRowMobility(Core.GameBoard board, int player)
        {
            int innerRow = player == 1 ? 1 : 2;
            int count = 0;

            for (int c = 0; c < 12; c++)
            {
                if (board.Get(innerRow, c) >= 1)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Score based on potential captures in the inner row.
        /// </summary>
        private float GetCaptureThreatScore(Core.GameBoard board, int player)
        {
            float threat = 0f;
            int playerInnerRow = player == 1 ? 1 : 2;
            int opponentInnerRow = player == 1 ? 2 : 1;
            int opponentOuterRow = player == 1 ? 3 : 0;

            for (int c = 0; c < 12; c++)
            {
                int opponentInnerStones = board.Get(opponentInnerRow, c);
                if (opponentInnerStones > 0)
                {
                    // Threat if we have a hole with stones that could land on empty inner hole.
                    // Outer row only adds to the threat if the inner row has stones to capture,
                    // matching the actual capture rule (no capture if inner row is empty).
                    if (board.Get(playerInnerRow, c) >= 1)
                    {
                        int opponentStones = opponentInnerStones + board.Get(opponentOuterRow, c);
                        threat += opponentStones * 0.1f;
                    }
                }
            }

            return threat;
        }

        /// <summary>
        /// Score relay potential (holes with many stones that could trigger relay).
        /// </summary>
        private float GetRelayPotential(Core.GameBoard board, int player)
        {
            float potential = 0f;
            int[] playerRows = player == 1 ? new[] { 0, 1 } : new[] { 2, 3 };

            foreach (int r in playerRows)
            {
                for (int c = 0; c < 12; c++)
                {
                    int stones = board.Get(r, c);
                    if (stones > 12)  // Holes with more than a full lap
                    {
                        potential += (stones - 12) * 0.05f;
                    }
                }
            }

            return potential;
        }
    }
}
