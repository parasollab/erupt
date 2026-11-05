using UnityEngine;

namespace Factory
{
    /// <summary>The one thing the user should do next in the factory demo.</summary>
    public enum GuideStep
    {
        Welcome,          // "Begin" on the placement panel: the card shows both controllers (trigger, grip, stick) and teaches pointing at a panel and pulling the trigger
        PlaceRobot,       // drag the floor ring until every reach marker is green
        ConfirmPlacement, // "Confirm placement" on the placement panel
        OpenDoors,        // "Open Doors" on the machine panel
        SelectBillet,     // point at the top billet, pull the trigger
        GrabBillet,       // grip the selected billet
        CarryToVise,      // carry it into the machine, let go between the jaws
        ReleaseInVise,    // released between the jaws: the plan is being sent
        Planning,         // MTC is planning
        Execute,          // "Execute" on the dashboard
        Executing,        // the robot is moving
        Clamp,            // "Clamp" on the machine panel
        CloseDoors,       // "Close Doors" on the machine panel: the job runs
        Machining,        // where the real machine would run the job (a short timer; the card says it is not simulated)
        OpenDoorsToUnload,// "Open Doors" again
        ReleaseVise,      // "Release" on the machine panel
        SelectMachinedBillet, // point at the billet in the vise, pull the trigger
        GrabMachinedBillet,   // grip it
        CarryToConveyor,  // carry it out of the machine onto the belt
        ReleaseOnConveyor,// released on the belt: the plan is being sent
        PlanningUnload,
        ExecuteUnload,
        ExecutingUnload,
        Done,
    }

    /// <summary>What the highlight and the off-screen chevron point at for a step.</summary>
    public enum GuideTarget { None, BeginButton, PlacementHandle, ConfirmButton, DoorOpenButton, Billet, ViseWindow, ExecuteButton, ClampButton, DoorCloseButton, ReleaseButton, MachinedBillet, Conveyor }

    /// <summary>
    /// Everything the step resolver looks at, read from the live components once per frame by
    /// <see cref="DemoGuide"/>. Kept as plain data so the resolver is a pure function and the
    /// step order can be tested without a scene.
    /// </summary>
    public struct GuideSignals
    {
        public bool welcomeDone;
        public bool placing;          // RobotPlacementController.Phase == Placing
        public bool handleGrabbed;
        public int reachable, targetCount;
        public bool robotBlocked;     // the robot's resting pose intersects the cell where it stands (RobotClearanceCheck)
        public bool robotTooFar;      // the vise is beyond the distance the planner can place through the door
        public string robotBlockedBy; // "link / obstacle" for the card
        public bool doorsOpen, doorsMoving;
        public bool billetSelected;   // the selection is one of the billets
        public bool billetGrabbed;    // a billet is in a hand (XRI grab or the editor's mouse drag)
        public bool billetInVise;     // a billet rests between the jaws and the robot is not holding it
        public bool recording;
        public PickPlacePhase clientPhase;
        public int solutionCount;
        public bool solutionLoading;  // solutions listed but the chosen one not fetched yet (guided dashboard)
        public bool loadedByRobot;    // the load execution finished successfully (guide bookkeeping)
        public bool machined;         // the job ran with the doors closed (guide bookkeeping, timer)
        public bool unloaded;         // the unload execution finished and the part came to rest (guide bookkeeping)
        public bool billetOnConveyor; // the machined billet rests on the conveyor's drop zone and nobody holds it
        public bool partSettling;     // a released part is still falling
        public bool viseClosed;
        public bool robotInJaws;      // the gripper is still inside the jaw window (clamp interlock)
    }

    public readonly struct GuideText
    {
        public readonly string title, instruction, controller, hint;
        public GuideText(string title, string instruction, string controller, string hint)
        {
            this.title = title; this.instruction = instruction; this.controller = controller; this.hint = hint;
        }
    }

    /// <summary>
    /// Derives the current step from the demo's state instead of counting button presses, so a
    /// step done out of order, undone (doors closed again, robot repositioned) or skipped by the
    /// docent always resolves to the right instruction. Order of precedence, top first:
    /// welcome, placement, the finished unload, the unload (after the job ran), the loaded part
    /// (clamp / doors / machining), the planner and the robot, the doors, the billet.
    /// </summary>
    public static class DemoGuideSteps
    {
        public const int StepCount = 16;

        public static GuideStep Resolve(in GuideSignals s)
        {
            if (!s.welcomeDone) return GuideStep.Welcome;
            if (s.placing)
            {
                if (s.handleGrabbed || s.reachable < s.targetCount || s.robotBlocked || s.robotTooFar) return GuideStep.PlaceRobot;
                return GuideStep.ConfirmPlacement;
            }
            if (s.unloaded) return GuideStep.Done;
            if (s.machined)
            {
                // The unload: the planner and the robot first, then the doors, the vise, the billet.
                if (s.clientPhase == PickPlacePhase.Executing || s.partSettling) return GuideStep.ExecutingUnload;
                if (s.clientPhase == PickPlacePhase.Planning) return GuideStep.PlanningUnload;
                if (s.solutionCount > 0) return s.solutionLoading ? GuideStep.PlanningUnload : GuideStep.ExecuteUnload;
                if (!s.doorsOpen) return GuideStep.OpenDoorsToUnload;
                if (s.viseClosed) return GuideStep.ReleaseVise;
                if (s.billetGrabbed) return GuideStep.CarryToConveyor;
                if (s.recording) return s.billetOnConveyor ? GuideStep.ReleaseOnConveyor : GuideStep.CarryToConveyor;
                if (!s.billetSelected) return GuideStep.SelectMachinedBillet;
                return GuideStep.GrabMachinedBillet;
            }
            if (s.loadedByRobot && s.billetInVise)
            {
                if (!s.viseClosed) return GuideStep.Clamp;
                return s.doorsOpen || s.doorsMoving ? GuideStep.CloseDoors : GuideStep.Machining;
            }
            if (s.clientPhase == PickPlacePhase.Executing || (s.loadedByRobot && s.partSettling)) return GuideStep.Executing;
            if (s.clientPhase == PickPlacePhase.Planning) return GuideStep.Planning;
            if (s.solutionCount > 0) return s.solutionLoading ? GuideStep.Planning : GuideStep.Execute;
            if (!s.doorsOpen) return GuideStep.OpenDoors;
            if (s.billetGrabbed) return GuideStep.CarryToVise;
            if (s.recording) return s.billetInVise ? GuideStep.ReleaseInVise : GuideStep.CarryToVise;
            if (!s.billetSelected) return GuideStep.SelectBillet;
            return GuideStep.GrabBillet;
        }

        public static GuideTarget TargetOf(GuideStep step) => step switch
        {
            GuideStep.Welcome => GuideTarget.BeginButton,
            GuideStep.PlaceRobot => GuideTarget.PlacementHandle,
            GuideStep.ConfirmPlacement => GuideTarget.ConfirmButton,
            GuideStep.OpenDoors => GuideTarget.DoorOpenButton,
            GuideStep.SelectBillet => GuideTarget.Billet,
            GuideStep.GrabBillet => GuideTarget.Billet,
            GuideStep.CarryToVise => GuideTarget.ViseWindow,
            GuideStep.ReleaseInVise => GuideTarget.ViseWindow,
            GuideStep.Execute => GuideTarget.ExecuteButton,
            GuideStep.Clamp => GuideTarget.ClampButton,
            GuideStep.CloseDoors => GuideTarget.DoorCloseButton,
            GuideStep.OpenDoorsToUnload => GuideTarget.DoorOpenButton,
            GuideStep.ReleaseVise => GuideTarget.ReleaseButton,
            GuideStep.SelectMachinedBillet => GuideTarget.MachinedBillet,
            GuideStep.GrabMachinedBillet => GuideTarget.MachinedBillet,
            GuideStep.CarryToConveyor => GuideTarget.Conveyor,
            GuideStep.ReleaseOnConveyor => GuideTarget.Conveyor,
            GuideStep.ExecuteUnload => GuideTarget.ExecuteButton,
            _ => GuideTarget.None,
        };

        /// <summary>1-based position on the card's "step N of 16" line; 0 for Done.</summary>
        public static int NumberOf(GuideStep step) => step switch
        {
            GuideStep.Welcome => 1,
            GuideStep.PlaceRobot => 2,
            GuideStep.ConfirmPlacement => 3,
            GuideStep.OpenDoors => 4,
            GuideStep.SelectBillet => 5,
            GuideStep.GrabBillet => 5,
            GuideStep.CarryToVise => 6,
            GuideStep.ReleaseInVise => 6,
            GuideStep.Planning => 7,
            GuideStep.Execute => 8,
            GuideStep.Executing => 8,
            GuideStep.Clamp => 9,
            GuideStep.CloseDoors => 10,
            GuideStep.Machining => 10,
            GuideStep.OpenDoorsToUnload => 11,
            GuideStep.ReleaseVise => 12,
            GuideStep.SelectMachinedBillet => 13,
            GuideStep.GrabMachinedBillet => 13,
            GuideStep.CarryToConveyor => 14,
            GuideStep.ReleaseOnConveyor => 14,
            GuideStep.PlanningUnload => 15,
            GuideStep.ExecuteUnload => 16,
            GuideStep.ExecutingUnload => 16,
            _ => 0,
        };

        /// <summary>The step is waiting on the robot or the planner, not on the user.</summary>
        public static bool IsWaiting(GuideStep step) =>
            step == GuideStep.Planning || step == GuideStep.Executing || step == GuideStep.ReleaseInVise
            || step == GuideStep.Machining || step == GuideStep.PlanningUnload || step == GuideStep.ExecutingUnload || step == GuideStep.ReleaseOnConveyor;

        /// <summary>Steps after the job ran: the unload.</summary>
        public static bool IsUnload(GuideStep step) => step >= GuideStep.OpenDoorsToUnload && step <= GuideStep.ExecutingUnload;

        /// <summary>"link / obstacle" from the clearance check as the card says it.</summary>
        public static string Obstacle(string blockedBy)
        {
            if (string.IsNullOrEmpty(blockedBy)) return "something";
            string obstacle = blockedBy.Contains("/") ? blockedBy.Substring(blockedBy.IndexOf('/') + 1).Trim() : blockedBy;
            string lower = obstacle.ToLowerInvariant();
            if (lower.StartsWith("vf2") || lower.Contains("door") || lower.Contains("body") || lower.Contains("machine")) return "the machine";
            if (lower.Contains("stock") || lower.Contains("stand") || lower.Contains("blank")) return "the stock stand";
            if (lower.Contains("conveyor")) return "the conveyor";
            if (lower.Contains("vise") || lower.Contains("jaw") || lower.Contains("fixture")) return "the vise";
            return obstacle;
        }

        const string Trigger = "TRIGGER = front button under your index finger.";
        const string Grip = "GRIP = side button under your middle finger.";
        const string PointAndTrigger = "Point the ray at the button and pull the TRIGGER.";
        const string StickMoves = "THUMBSTICK forward or back pushes it away or pulls it closer.";
        const string Reading = "Good. Hold still: the robot is reading where you put it...";
        const string Loading = "The robot found a way. Loading the motion...";
        const string Offline = "Waiting for the robot connection... (ask the demonstration administrator)";

        /// <summary>Card text for a step; <paramref name="s"/> picks the variant (grabbed, moving, ...). Kept short: the card is narrow.</summary>
        public static GuideText Describe(GuideStep step, in GuideSignals s, bool robotOnline)
        {
            switch (step)
            {
                case GuideStep.Welcome:
                    // The card shows both controllers under this text (GuideInstructionCard): trigger, grip and thumbstick ringed, the ray from the front.
                    return new GuideText("Welcome",
                        "You will teach this robot to load a block into the milling machine. These are your two controllers; both work the same, so use whichever hand you prefer. Point the ray at the panel on your left and press 'Begin'.",
                        PointAndTrigger + " " + Trigger,
                        "Hold a controller like a flashlight: trigger under your index finger, grip under your middle finger, the ray out of the front. Turn left to find the panel.");
                case GuideStep.PlaceRobot:
                    if (s.handleGrabbed)
                        return new GuideText("Place the robot",
                            $"Keep dragging: {s.reachable} of {s.targetCount} markers are green." +
                            (s.robotBlocked ? " Red ring: the robot would touch " + Obstacle(s.robotBlockedBy) + " here." : s.robotTooFar ? " Red ring: too far to reach the vise, come closer to the doors." : "") +
                            " Release the GRIP when all six are green and the ring is blue.",
                            "Hold GRIP to drag; turn your wrist to turn the robot.",
                            "Right in front of the machine doors, centred, reaches everything.");
                    if (s.reachable >= s.targetCount && !s.robotBlocked && s.robotTooFar)
                        return new GuideText("Place the robot",
                            "All six markers are green but the ring is red: the robot cannot reach the vise from here. Drag the ring closer to the machine doors until it turns blue.",
                            Grip + " Point at the ring first.",
                            "Right in front of the doors, about a step away.");
                    if (s.reachable >= s.targetCount && s.robotBlocked)
                        return new GuideText("Place the robot",
                            $"All six markers are green but the ring is red: here the robot's arm touches {Obstacle(s.robotBlockedBy)}. Drag the ring a little away from it until it turns blue.",
                            Grip + " Point at the ring first; turn your wrist to turn the robot.",
                            "Centred in front of the doors, a step back, square to the machine.");
                    return new GuideText("Place the robot",
                        "The six floating balls are the spots the robot must reach: the blocks on the stand, the vise and the conveyor. Red means out of reach. The ring around the robot is red while it cannot do the job from here and turns blue when it can. Point at the ring, hold the GRIP and drag the robot until all six balls are green and the ring is blue.",
                        Grip + " Point at the red ring on the floor first. Turn your wrist to turn the robot.",
                        "Try the spot right in front of the machine doors.");
                case GuideStep.ConfirmPlacement:
                    return new GuideText("Confirm the placement",
                        "All six markers are green. Press 'Confirm placement' on the panel to your left.",
                        PointAndTrigger,
                        "The panel is on your left; the ray comes out of the front of the controller.");
                case GuideStep.OpenDoors:
                    if (s.doorsMoving)
                        return new GuideText("Open the machine", "The doors are moving...", "", "");
                    return new GuideText("Open the machine",
                        "Press 'Open Doors' on the machine panel to your left.",
                        PointAndTrigger,
                        "Top green button on the left panel.");
                case GuideStep.SelectBillet:
                    return new GuideText("Pick a block",
                        "Point the ray at the top block on the stand and pull the TRIGGER once to select it.",
                        Trigger + " The block lights up when selected.",
                        "The blocks are the grey stack on the stand in front of the robot. The ray must touch the block when you pull the trigger.");
                case GuideStep.GrabBillet:
                    return new GuideText("Pick up the block",
                        "Point at the selected block and squeeze the GRIP to pick it up.",
                        Grip + " Keep it squeezed while you carry. " + StickMoves,
                        "Squeeze the side button while the ray points at the lit block. Walk closer if the ray will not reach.");
                case GuideStep.CarryToVise:
                    if (!s.billetGrabbed && s.recording)
                        return new GuideText("Show the robot where it goes",
                            "Not there yet. Pick the block up again and set it between the two jaws of the vise inside the machine.",
                            Grip + " Release the GRIP to put it down.",
                            "The vise is on the round table behind the open doors; the block goes between the two dark jaws, on the plate.");
                    return new GuideText("Show the robot where it goes",
                        "Carry the block into the machine and release the GRIP with it between the two open jaws of the vise.",
                        StickMoves + " Release the GRIP to put it down.",
                        "The vise is on the round table behind the open doors. Lower the block between the two dark jaws, then let go. Walk or lean closer if needed.");
                case GuideStep.ReleaseInVise:
                    return new GuideText("Show the robot where it goes", Reading, "", "");
                case GuideStep.Planning:
                    if (s.solutionLoading) return new GuideText("The robot is thinking", Loading, "", "");
                    return new GuideText("The robot is thinking",
                        robotOnline ? "The robot is planning the same move. This takes a few seconds." : Offline, "", "");
                case GuideStep.Execute:
                    return new GuideText("Let the robot do it",
                        "The robot found a way. Press 'Execute' on the panel to your right.",
                        PointAndTrigger,
                        "Green 'Execute' button near the bottom of the right panel.");
                case GuideStep.Executing:
                    return new GuideText("Watch the robot",
                        "The robot is loading the block. Stand back and watch.",
                        "", "Nothing to do until it is done.");
                case GuideStep.Clamp:
                    if (s.robotInJaws)
                        return new GuideText("Clamp the block", "Wait for the robot to move clear of the vise...", "", "");
                    return new GuideText("Clamp the block",
                        "The block is in the vise. Press 'Clamp' on the machine panel to your left.",
                        PointAndTrigger,
                        "Green 'Clamp' button in the vise section of the left panel.");
                case GuideStep.CloseDoors:
                    if (s.doorsMoving)
                        return new GuideText("Close the machine", "The doors are moving...", "", "");
                    return new GuideText("Close the machine",
                        "Press 'Close Doors' on the machine panel so the machine can run the job.",
                        PointAndTrigger,
                        "Red 'Close Doors' button on the left panel.");
                case GuideStep.Machining:
                    return new GuideText("Machining (not simulated)",
                        "This is where the real machine would mill the block. The demo does not simulate the cut and moves on to unloading in a moment.",
                        "", "");
                case GuideStep.OpenDoorsToUnload:
                    if (s.doorsMoving)
                        return new GuideText("Unload the machine", "The doors are moving...", "", "");
                    return new GuideText("Unload the machine",
                        "The job is done. Press 'Open Doors' on the machine panel to get the part out.",
                        PointAndTrigger,
                        "Green 'Open Doors' button at the top of the left panel.");
                case GuideStep.ReleaseVise:
                    return new GuideText("Release the vise",
                        "Press 'Release' on the machine panel so the robot can take the part.",
                        PointAndTrigger,
                        "Red 'Release' button in the vise section of the left panel.");
                case GuideStep.SelectMachinedBillet:
                    return new GuideText("Pick the finished part",
                        "Point the ray at the part in the vise and pull the TRIGGER once to select it.",
                        Trigger + " The part lights up when selected.",
                        "It sits between the vise jaws inside the machine; the ray must touch it when you pull the trigger.");
                case GuideStep.GrabMachinedBillet:
                    return new GuideText("Pick up the finished part",
                        "Point at the selected part and squeeze the GRIP to pick it up.",
                        Grip + " Keep it squeezed while you carry. " + StickMoves,
                        "Squeeze the side button while the ray points at the lit part. Lean closer if the ray will not reach.");
                case GuideStep.CarryToConveyor:
                    if (!s.billetGrabbed && s.recording)
                        return new GuideText("Show the robot where it goes",
                            "Not there yet. Pick the part up again and set it on the conveyor belt to the left of the robot.",
                            Grip + " Release the GRIP to put it down.",
                            "The belt runs from beside the robot to the table on the far left. Set the part on it near the robot.");
                    return new GuideText("Show the robot where it goes",
                        "Carry the part out of the machine and release the GRIP with it resting on the conveyor belt.",
                        StickMoves + " Release the GRIP to put it down.",
                        "The conveyor is the belt to the left of the robot. Lower the part onto it, then let go.");
                case GuideStep.ReleaseOnConveyor:
                    return new GuideText("Show the robot where it goes", Reading, "", "");
                case GuideStep.PlanningUnload:
                    if (s.solutionLoading) return new GuideText("The robot is thinking", Loading, "", "");
                    return new GuideText("The robot is thinking",
                        robotOnline ? "The robot is planning the unload. This takes a few seconds." : Offline, "", "");
                case GuideStep.ExecuteUnload:
                    return new GuideText("Let the robot do it",
                        "The robot found a way. Press 'Execute' on the panel to your right.",
                        PointAndTrigger,
                        "Green 'Execute' button near the bottom of the right panel.");
                case GuideStep.ExecutingUnload:
                    return new GuideText("Watch the robot",
                        "The robot is unloading the part onto the conveyor. Stand back and watch.",
                        "", "Nothing to do until it is done.");
                case GuideStep.Done:
                    return new GuideText("Demo finished",
                        "The demo is finished: you taught the robot to load the machine and to unload the finished part.",
                        "", "");
            }
            return new GuideText(step.ToString(), "", "", "");
        }
    }
}
