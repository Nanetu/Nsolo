using System.Threading;
using System.Threading.Tasks;
using NsoloGame.Core;

namespace NsoloGame.Players
{
    /// <summary>
    /// A person tapping the board, behind the same contract as the AI.
    ///
    /// The trick is a <see cref="TaskCompletionSource{TResult}"/>: RequestMove hands back a task
    /// that is simply not finished yet, and the tap that lands later is what completes it. So an
    /// event-driven input source satisfies an interface shaped around "go and compute a move",
    /// without the turn manager polling for input or the AI path changing to accommodate it.
    ///
    /// This validates nothing. The caller decides which moves are legal before submitting one —
    /// the same rule that already governs taps in single-player.
    /// </summary>
    public class LocalHumanAgent : IPlayerAgent
    {
        private TaskCompletionSource<Move> pending;
        private CancellationTokenRegistration cancellationRegistration;

        public LocalHumanAgent(string displayName)
        {
            DisplayName = displayName;
        }

        public string DisplayName { get; }

        public bool RequiresBoardInput => true;

        /// <summary>True while a move has been asked for and no tap has answered it yet.</summary>
        public bool IsAwaitingInput => pending != null && !pending.Task.IsCompleted;

        public Task<Move> RequestMove(GameBoard board, int player, CancellationToken token)
        {
            // Any previous request is abandoned rather than left dangling — otherwise restarting
            // mid-turn would leave a task nothing will ever complete.
            Abandon();

            // RunContinuationsAsynchronously keeps whatever resumes on the task from running inside
            // SubmitMove, which is called from the middle of Unity's input handling.
            pending = new TaskCompletionSource<Move>(TaskCreationOptions.RunContinuationsAsynchronously);

            TaskCompletionSource<Move> captured = pending;
            cancellationRegistration = token.Register(() => captured.TrySetCanceled());

            return pending.Task;
        }

        /// <summary>
        /// Answers the outstanding request with the player's chosen move. Returns false when no
        /// move was being waited on, so a stray tap is a no-op rather than an error.
        /// </summary>
        public bool SubmitMove(Move move)
        {
            if (pending == null || pending.Task.IsCompleted) return false;

            cancellationRegistration.Dispose();
            cancellationRegistration = default;
            return pending.TrySetResult(move);
        }

        /// <summary>
        /// Drops the outstanding request. Used when a game ends or restarts while a player still
        /// had the board in front of them.
        /// </summary>
        public void Abandon()
        {
            cancellationRegistration.Dispose();
            cancellationRegistration = default;

            pending?.TrySetCanceled();
            pending = null;
        }
    }
}
