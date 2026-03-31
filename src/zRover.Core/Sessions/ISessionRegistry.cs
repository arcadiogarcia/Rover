using System;
using System.Collections.Generic;

namespace zRover.Core.Sessions
{
    /// <summary>
    /// Manages the set of connected <see cref="IRoverSession"/> instances and tracks
    /// which one is currently active (receives forwarded tool calls).
    /// </summary>
    public interface ISessionRegistry
    {
        /// <summary>Snapshot of all currently connected sessions.</summary>
        IReadOnlyList<IRoverSession> Sessions { get; }

        /// <summary>
        /// The session that receives forwarded tool calls.
        /// Null until explicitly set via <see cref="TrySetActive"/>.
        /// </summary>
        IRoverSession? ActiveSession { get; }

        /// <summary>Adds a new session to the registry.</summary>
        void Add(IRoverSession session);

        /// <summary>
        /// Removes the session with the given ID.
        /// If it was the active session, <see cref="ActiveSession"/> is set to null
        /// and <see cref="ActiveSessionChanged"/> is fired.
        /// </summary>
        bool Remove(string sessionId);

        /// <summary>
        /// Makes the session with the given ID the active one.
        /// Returns false if no session with that ID exists.
        /// Fires <see cref="ActiveSessionChanged"/> when the active session changes.
        /// </summary>
        bool TrySetActive(string sessionId);

        /// <summary>
        /// Fired when <see cref="ActiveSession"/> changes — either explicitly via
        /// <see cref="TrySetActive"/> or automatically because the active session
        /// disconnected.
        /// </summary>
        event EventHandler<ActiveSessionChangedEventArgs>? ActiveSessionChanged;

        /// <summary>
        /// Fired when a session is added or removed.
        /// </summary>
        event EventHandler? SessionsChanged;
    }

    public sealed class ActiveSessionChangedEventArgs : EventArgs
    {
        public IRoverSession? Previous { get; }
        public IRoverSession? Current { get; }

        public ActiveSessionChangedEventArgs(IRoverSession? previous, IRoverSession? current)
        {
            Previous = previous;
            Current = current;
        }
    }
}
