using System;
using System.Collections.Generic;

namespace HeroCat.Runtime.Errors
{
    /// <summary>
    /// The presentation rules of 01 section 7 that are policy, not rendering (07 renders): never two modals at once (a
    /// second modal-class error waits until the first is dismissed), a Reject never opens a modal, and the same code is
    /// not queued twice while it is showing or waiting. Inline rejects, cards and silent errors pass straight through to
    /// <see cref="Inline"/>. Main thread only.
    /// </summary>
    public sealed class ErrorPolicy
    {
        readonly Queue<UserError> _waiting = new Queue<UserError>();

        /// <summary>The modal on screen, or null.</summary>
        public UserError CurrentModal { get; private set; }

        public int WaitingCount => _waiting.Count;

        /// <summary>Raised for every non-modal error (inline shake + toast, offline card); 07 places it.</summary>
        public event Action<UserError> Inline;

        /// <summary>Raised when a modal becomes the current one.</summary>
        public event Action<UserError> ModalShown;

        /// <summary>Routes <paramref name="e"/>. Returns true when it was shown now, false when queued or dropped.</summary>
        public bool Report(UserError e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            if (e.Presentation == Presentation.None) return false;
            if (!e.IsModal || e.Class == ErrorClass.Reject)
            {
                Inline?.Invoke(e);
                return true;
            }
            if (CurrentModal != null)
            {
                if (CurrentModal.Code == e.Code) return false;
                foreach (var w in _waiting) if (w.Code == e.Code) return false;
                // A fatal or blocking error replaces whatever is showing: the app cannot continue past it.
                if (e.Presentation == Presentation.Fatal && CurrentModal.Presentation != Presentation.Fatal)
                {
                    CurrentModal = e;
                    ModalShown?.Invoke(e);
                    return true;
                }
                _waiting.Enqueue(e);
                return false;
            }
            CurrentModal = e;
            ModalShown?.Invoke(e);
            return true;
        }

        /// <summary>The player closed the current modal; the next waiting one (if any) is shown.</summary>
        public void Dismiss()
        {
            CurrentModal = null;
            if (_waiting.Count == 0) return;
            CurrentModal = _waiting.Dequeue();
            ModalShown?.Invoke(CurrentModal);
        }
    }
}
