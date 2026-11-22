using System;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Robot
{
    /// <summary>
    /// Puts a robot's base at a world pose (<see cref="RobotInstance.PlaceBase(Pose, out string)"/>),
    /// undoably: undo puts the base back where it was. Hand it to <see cref="UndoStack.Do"/>.
    /// </summary>
    /// <remarks>
    /// The robot may refuse (it is executing, it carries something). The command then does
    /// nothing and says why through <see cref="Refused"/> and <see cref="LastRefusal"/>; check
    /// <see cref="RobotInstance.CanPlaceBase"/> first to keep a refused command out of the
    /// history.
    /// </remarks>
    public sealed class PlaceBaseCommand : IUndoableCommand
    {
        private readonly RobotInstance robot;
        private readonly Pose target;
        private Pose previous;
        private bool applied;

        /// <summary>A move (or its undo) was refused; the argument is the reason, as a sentence for the user.</summary>
        public event Action<string> Refused;

        /// <summary>Why the last <see cref="Execute"/> or <see cref="Undo"/> did nothing; null when it went through.</summary>
        public string LastRefusal { get; private set; }

        public PlaceBaseCommand(RobotInstance robot, Pose target)
        {
            this.robot = robot != null ? robot : throw new ArgumentNullException(nameof(robot));
            this.target = target;
            previous = robot.BasePose;
        }

        public string Label => robot != null ? $"Place {robot.Id}" : "Place robot";

        /// <summary>The base pose the robot had before the command ran.</summary>
        public Pose Previous => previous;

        public void Execute()
        {
            if (robot == null) return;
            Pose before = robot.BasePose;
            if (!Move(target)) return;
            previous = before;
            applied = true;
        }

        public void Undo()
        {
            if (robot == null || !applied) return;
            if (Move(previous)) applied = false;
        }

        private bool Move(Pose pose)
        {
            if (robot.PlaceBase(pose, out string reason))
            {
                LastRefusal = null;
                return true;
            }
            LastRefusal = reason;
            Refused?.Invoke(reason);
            return false;
        }
    }
}
