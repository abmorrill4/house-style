using System;
using System.Collections.Generic;

namespace FlareAI.Flare.UI
{
    public enum PaneSlot { Topic, Project }

    /// <summary>
    /// A slot model holding two panels (Topic and Project), managing which one is shown and whether the pane is open.
    /// Never holds or exposes WinForms types directly, so routing decisions are testable without Flare.
    /// </summary>
    public sealed class PaneSlots<T> where T : class
    {
        private T _topic;
        private T _project;
        private PaneSlot _shown;
        private bool _isOpen;
        private bool _everFilled;

        public T Topic { get { return _topic; } }
        public T Project { get { return _project; } }

        /// <summary>The slot shown to the user. Meaningful once a slot is filled.</summary>
        public PaneSlot Shown { get { return _shown; } }

        /// <summary>False after Close().</summary>
        public bool IsOpen { get { return _isOpen; } }

        /// <summary>Get the current panel in a slot, or null if the slot is empty.</summary>
        public T Get(PaneSlot slot)
        {
            return slot == PaneSlot.Topic ? _topic : _project;
        }

        /// <summary>Get the panel currently shown, or null if that slot is empty or closed.</summary>
        public T ShownPanel
        {
            get { return _isOpen ? Get(_shown) : null; }
        }

        /// <summary>
        /// Put a panel in a slot. Returns the old panel of the same slot (null if none or same instance).
        /// Never returns the other slot's panel. Sets Shown and IsOpen based on front flag.
        /// </summary>
        public T Put(PaneSlot slot, T panel, bool front)
        {
            if (panel == null) throw new ArgumentNullException("panel");

            T old;
            if (slot == PaneSlot.Topic)
            {
                old = _topic;
                _topic = panel;
            }
            else
            {
                old = _project;
                _project = panel;
            }

            T result = (old == panel) ? null : old; // Same instance returns null.

            _everFilled = true;
            if (front)
            {
                _shown = slot;
                _isOpen = true;
            }

            return result;
        }

        /// <summary>
        /// Show a slot. Returns false if that slot is empty. Otherwise sets Shown and returns true.
        /// </summary>
        public bool Show(PaneSlot slot)
        {
            if (Get(slot) == null) return false;
            _shown = slot;
            return true;
        }

        /// <summary>Close the pane. IsOpen becomes false; both slots and Shown are kept.</summary>
        public void Close()
        {
            _isOpen = false;
        }

        /// <summary>
        /// Reopen the pane. If Shown slot is filled, IsOpen=true and returns true with slot=Shown.
        /// Else if the other slot is filled, IsOpen=true and returns true with slot=other.
        /// Else returns false (caller falls back to Lint Topic).
        /// </summary>
        public bool TryReopen(out PaneSlot slot)
        {
            if (!_everFilled)
            {
                slot = PaneSlot.Topic;
                return false;
            }

            if (Get(_shown) != null)
            {
                slot = _shown;
                _isOpen = true;
                return true;
            }

            var other = _shown == PaneSlot.Topic ? PaneSlot.Project : PaneSlot.Topic;
            if (Get(other) != null)
            {
                slot = other;
                _shown = other;
                _isOpen = true;
                return true;
            }

            slot = PaneSlot.Topic;
            return false;
        }

        /// <summary>True if both slots are filled.</summary>
        public bool BothFilled
        {
            get { return _topic != null && _project != null; }
        }

        /// <summary>True if Topic slot is filled. A topic switch only replaces, never creates.</summary>
        public bool CanReplaceTopic
        {
            get { return _topic != null; }
        }

        /// <summary>
        /// Remove and return the Topic panel if matches(Topic) is true, else return null and keep Topic.
        /// If Shown was Topic and Project exists, set Shown=Project.
        /// </summary>
        public T ClearTopicIf(Func<T, bool> matches)
        {
            if (_topic == null || !matches(_topic)) return null;

            T old = _topic;
            _topic = null;

            if (_shown == PaneSlot.Topic && _project != null)
            {
                _shown = PaneSlot.Project;
            }

            return old;
        }

        /// <summary>Return filled slots in order: Topic first, then Project. Used for recheck and marker routes.</summary>
        public IList<T> Both()
        {
            var result = new List<T>();
            if (_topic != null) result.Add(_topic);
            if (_project != null) result.Add(_project);
            return result;
        }
    }
}
