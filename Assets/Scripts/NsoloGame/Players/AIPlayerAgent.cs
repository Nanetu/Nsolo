using System.Threading;
using System.Threading.Tasks;
using NsoloGame.AI;
using NsoloGame.Core;

namespace NsoloGame.Players
{
    /// <summary>
    /// Presents the existing <see cref="AIAgent"/> through <see cref="IPlayerAgent"/>.
    ///
    /// Deliberately a wrapper and not a change to AIAgent. The search — iterative deepening,
    /// alpha-beta, the transposition table, the evaluation function — is the part of this project
    /// that is actually being assessed, so it stays exactly as it was and keeps its own API. This
    /// class only moves the call onto a background task, which is what GameController was already
    /// doing at the call site.
    /// </summary>
    public class AIPlayerAgent : IPlayerAgent
    {
        private readonly AIAgent agent;

        public AIPlayerAgent(AIAgent agent)
        {
            this.agent = agent;
        }

        // "Computer" rather than "AI" — this string is shown to players, and "AI" carries baggage
        // for a lot of people that a board-game opponent should not be dragging along.
        public string DisplayName => "Computer";

        public bool RequiresBoardInput => false;

        public Task<Move> RequestMove(GameBoard board, int player, CancellationToken token)
        {
            // SelectMove is synchronous and CPU-bound, so it goes to the thread pool rather than
            // blocking the frame — the same arrangement GameController.StartAIMove uses today.
            return Task.Run(() => agent.SelectMove(board, player, token), token);
        }
    }
}
