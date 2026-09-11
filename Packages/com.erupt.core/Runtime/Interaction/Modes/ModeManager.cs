using System;
using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// Current mode, and the only way to change it. Guidelines Part 4.
    /// </summary>
    /// <remarks>
    /// Part 4's rules are structural, not stylistic: switching is one action and always
    /// reversible, and there are no hidden modes — "an invisible mode is a bug". The
    /// tier 1 indicator and the robot outline colour both read <see cref="Current"/>,
    /// so a mode cannot exist without being displayed.
    /// </remarks>
    public class ModeManager : MonoBehaviour
    {
        [SerializeField] private AppMode initialMode = AppMode.Build;

        private bool started;

        public AppMode Current { get; private set; }

        /// <summary>The mode active before the last switch, so a change is always reversible.</summary>
        public AppMode Previous { get; private set; }

        public event Action<AppMode> ModeChanged;

        private void Awake()
        {
            Current = initialMode;
            Previous = initialMode;
            started = true;
        }

        public void SetMode(AppMode mode)
        {
            if (!started) { Current = Previous = mode; return; }
            if (mode == Current) return;

            Previous = Current;
            Current = mode;
            ModeChanged?.Invoke(Current);
        }

        /// <summary>Return to the mode active before the last switch. Part 4: always reversible.</summary>
        public void Revert() => SetMode(Previous);

        /// <summary>Advance Build -> Plan -> Teach -> Build. Part 4: switching is one action.</summary>
        public void Cycle()
        {
            SetMode(Current switch
            {
                AppMode.Build => AppMode.Plan,
                AppMode.Plan => AppMode.Teach,
                _ => AppMode.Build
            });
        }

        public bool Is(AppMode mode) => Current == mode;

        private void OnDestroy() => ModeChanged = null;
    }
}
