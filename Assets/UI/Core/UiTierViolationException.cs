using System;

namespace Erupt.Ui
{
    /// <summary>
    /// Thrown when a tier invariant is broken. Guidelines Part 2 makes the tier 1 cap a
    /// hard limit, so a fifth control fails loudly instead of logging and carrying on.
    /// </summary>
    public class UiTierViolationException : Exception
    {
        public UiTierViolationException(string message) : base(message) { }
    }
}
