using System.Threading;
using System.Threading.Tasks;
using NsoloGame.Core;

namespace NsoloGame.Players
{
    /// <summary>
    /// One side of a game, from the turn manager's point of view: hand it a board and it eventually
    /// hands back a move.
    ///
    /// The contract is asynchronous for everyone, which is what lets the turn manager stop caring
    /// who is playing. The AI resolves its task when minimax finishes; a local human resolves it
    /// when a pit is tapped; a network opponent would resolve it when the move arrives off the
    /// wire. None of those need the turn manager to know which it is dealing with.
    ///
    /// Implementations must not touch the board they are given — it is a snapshot the caller may
    /// still be using.
    /// </summary>
    public interface IPlayerAgent
    {
        /// <summary>Shown in status text, e.g. "Player 2" or "Computer".</summary>
        string DisplayName { get; }

        /// <summary>
        /// Whether this agent's move comes from taps on the board. The turn manager uses it to
        /// decide whether to highlight legal pits and accept board input; it is false for the AI
        /// and would be false for a network opponent.
        /// </summary>
        bool RequiresBoardInput { get; }

        /// <summary>
        /// Asks for this agent's move. The returned task completes with the chosen move, or is
        /// cancelled if <paramref name="token"/> fires — which is how a turn is abandoned when the
        /// game is restarted or left mid-think.
        /// </summary>
        Task<Move> RequestMove(GameBoard board, int player, CancellationToken token);
    }
}
