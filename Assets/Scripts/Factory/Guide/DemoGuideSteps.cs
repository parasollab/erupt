using UnityEngine;

namespace Factory
{
    /// <summary>The one thing the user should do next in the factory demo.</summary>
    public enum GuideStep
    {
        Welcome,          // "Begin" on the placement panel: teaches pointing at a panel and pulling the trigger
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
        Machining,        // the machine runs the job (a timer)
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
        public bool doorsOpen, doorsMoving;
        public bool billetSelected;   // the selection is one of the billets
        public bool billetGrabbed;    // a billet is in a hand (XRI grab or the editor's mouse drag)
        public bool billetInVise;     // a billet rests between the jaws and the robot is not holding it
        public bool recording;
        public PickPlacePhase clientPhase;
        public int solutionCount;
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
                if (s.handleGrabbed || s.reachable < s.targetCount) return GuideStep.PlaceRobot;
                return GuideStep.ConfirmPlacement;
            }
            if (s.unloaded) return GuideStep.Done;
            if (s.machined)
            {
                // The unload: the planner and the robot first, then the doors, the vise, the billet.
                if (s.clientPhase == PickPlacePhase.Executing || s.partSettling) return GuideStep.ExecutingUnload;
                if (s.clientPhase == PickPlacePhase.Planning) return GuideStep.PlanningUnload;
                if (s.solutionCount > 0) return GuideStep.ExecuteUnload;
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
            if (s.solutionCount > 0) return GuideStep.Execute;
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

        /// <summary>1-based position on the card's "step N of 10" line; 0 for Done.</summary>
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

        const string Trigger = "TRIGGER = the front button under your index finger.";
        const string Grip = "GRIP = the side button under your middle finger.";
        const string PointAndTrigger = "Point the ray at the button and pull the TRIGGER.";

        /// <summary>Card text for a step; <paramref name="s"/> picks the variant (grabbed, moving, ...).</summary>
        public static GuideText Describe(GuideStep step, in GuideSignals s, bool robotOnline)
        {
            switch (step)
            {
                case GuideStep.Welcome:
                    return new GuideText("Welcome",
                        "You will teach this robot to load a part into the milling machine. Point the ray at the panel on your left and press 'Begin'.",
                        PointAndTrigger + " " + Trigger,
                        "Hold the controller like a pistol; the ray comes out of its front. Turn left to find the panel, point at the green button and pull the trigger.");
                case GuideStep.PlaceRobot:
                    if (s.handleGrabbed)
                        return new GuideText("Place the robot",
                            $"Keep dragging. {s.reachable} of {s.targetCount} markers are green. Let go of the GRIP when they all are.",
                            "Hold GRIP to keep dragging; release it to let go.",
                            "The spot right in front of the machine doors, centred, reaches everything.");
                    return new GuideText("Place the robot",
                        "Point the ray at the blue ring on the floor, hold the GRIP and drag the robot until all six markers turn green.",
                        Grip + " Point at the ring first.",
                        "The markers turn green when the robot can reach them. Try the spot right in front of the machine doors.");
                case GuideStep.ConfirmPlacement:
                    return new GuideText("Confirm the placement",
                        "All six markers are green. Press 'Confirm placement' on the panel to your left.",
                        PointAndTrigger,
                        "The panel is on your left. The ray comes out of the front of the controller.");
                case GuideStep.OpenDoors:
                    if (s.doorsMoving)
                        return new GuideText("Open the machine", "The doors are moving...", "", "");
                    return new GuideText("Open the machine",
                        "Press 'Open Doors' on the machine panel to your left.",
                        PointAndTrigger,
                        "The machine panel is the panel on your left, top green button.");
                case GuideStep.SelectBillet:
                    return new GuideText("Pick a billet",
                        "Point the ray at the top billet on the stand and pull the TRIGGER once to select it.",
                        Trigger + " The billet lights up when it is selected.",
                        "The billets are the grey blocks stacked on the stand in front of the robot. The ray must touch the block when you pull the trigger.");
                case GuideStep.GrabBillet:
                    return new GuideText("Pick up the billet",
                        "Point at the selected billet and squeeze the GRIP to pick it up.",
                        Grip + " Keep it squeezed while you carry.",
                        "Squeeze the side button while the ray points at the highlighted billet. You can pull it closer by walking.");
                case GuideStep.CarryToVise:
                    if (!s.billetGrabbed && s.recording)
                        return new GuideText("Show the robot where it goes",
                            "Not there yet. Pick the billet up again and set it down between the two jaws of the vise inside the machine.",
                            Grip + " Let go of the GRIP to put it down.",
                            "The vise is on the round table behind the open doors. The billet has to sit between the two dark jaws, on the plate.");
                    return new GuideText("Show the robot where it goes",
                        "Carry the billet into the machine and let go of the GRIP with it sitting between the two open jaws of the vise.",
                        "Release the GRIP to put it down. Walk or lean closer if you need to.",
                        "The vise is on the round table behind the open doors. Lower the billet between the two dark jaws, then let go.");
                case GuideStep.ReleaseInVise:
                    return new GuideText("Show the robot where it goes",
                        "Good. Hold still a moment: the robot is reading where you put it...",
                        "", "");
                case GuideStep.Planning:
                    return new GuideText("The robot is thinking",
                        robotOnline ? "The robot is planning how to do the same move. This takes a few seconds."
                                    : "Waiting for the robot connection... (ask the person next to you)",
                        "", "");
                case GuideStep.Execute:
                    return new GuideText("Let the robot do it",
                        "The robot found a way. Press 'Execute' on the panel to your right to let it load the billet.",
                        PointAndTrigger,
                        "The panel on your right: the green 'Execute' button near the bottom.");
                case GuideStep.Executing:
                    return new GuideText("Watch the robot",
                        "The robot is loading the billet. Stand back and watch.",
                        "", "The robot moves on its own now; nothing to do until it is done.");
                case GuideStep.Clamp:
                    if (s.robotInJaws)
                        return new GuideText("Clamp the billet", "Wait for the robot to move clear of the vise...", "", "");
                    return new GuideText("Clamp the billet",
                        "The billet is in the vise. Press 'Clamp' on the machine panel to your left to hold it.",
                        PointAndTrigger,
                        "The machine panel on your left, the green 'Clamp' button in the vise section.");
                case GuideStep.CloseDoors:
                    if (s.doorsMoving)
                        return new GuideText("Close the machine", "The doors are moving...", "", "");
                    return new GuideText("Close the machine",
                        "Press 'Close Doors' on the machine panel so the machine can run the job.",
                        PointAndTrigger,
                        "The machine panel on your left, the red 'Close Doors' button.");
                case GuideStep.Machining:
                    return new GuideText("Machining", "The machine is running the job. Wait for it to finish...", "", "");
                case GuideStep.OpenDoorsToUnload:
                    if (s.doorsMoving)
                        return new GuideText("Unload the machine", "The doors are moving...", "", "");
                    return new GuideText("Unload the machine",
                        "The job is done. Press 'Open Doors' on the machine panel to get the part out.",
                        PointAndTrigger,
                        "The machine panel on your left, the green 'Open Doors' button at the top.");
                case GuideStep.ReleaseVise:
                    return new GuideText("Release the vise",
                        "Press 'Release' on the machine panel so the robot can take the part.",
                        PointAndTrigger,
                        "The machine panel on your left, the red 'Release' button in the vise section.");
                case GuideStep.SelectMachinedBillet:
                    return new GuideText("Pick the finished part",
                        "Point the ray at the part in the vise and pull the TRIGGER once to select it.",
                        Trigger + " The part lights up when it is selected.",
                        "The part sits between the vise jaws inside the machine. The ray must touch it when you pull the trigger.");
                case GuideStep.GrabMachinedBillet:
                    return new GuideText("Pick up the finished part",
                        "Point at the selected part and squeeze the GRIP to pick it up.",
                        Grip + " Keep it squeezed while you carry.",
                        "Squeeze the side button while the ray points at the highlighted part. Lean closer if the ray will not reach.");
                case GuideStep.CarryToConveyor:
                    if (!s.billetGrabbed && s.recording)
                        return new GuideText("Show the robot where it goes",
                            "Not there yet. Pick the part up again and set it down on the conveyor belt to the left of the robot.",
                            Grip + " Let go of the GRIP to put it down.",
                            "The conveyor is the belt running from beside the robot to the table on the far left. Set the part on the belt near the robot.");
                    return new GuideText("Show the robot where it goes",
                        "Carry the part out of the machine and let go of the GRIP with it resting on the conveyor belt.",
                        "Release the GRIP to put it down. Walk or lean closer if you need to.",
                        "The conveyor is the belt to the left of the robot. Lower the part onto it, then let go.");
                case GuideStep.ReleaseOnConveyor:
                    return new GuideText("Show the robot where it goes",
                        "Good. Hold still a moment: the robot is reading where you put it...",
                        "", "");
                case GuideStep.PlanningUnload:
                    return new GuideText("The robot is thinking",
                        robotOnline ? "The robot is planning how to unload the part. This takes a few seconds."
                                    : "Waiting for the robot connection... (ask the person next to you)",
                        "", "");
                case GuideStep.ExecuteUnload:
                    return new GuideText("Let the robot do it",
                        "The robot found a way. Press 'Execute' on the panel to your right to let it unload the part.",
                        PointAndTrigger,
                        "The panel on your right: the green 'Execute' button near the bottom.");
                case GuideStep.ExecutingUnload:
                    return new GuideText("Watch the robot",
                        "The robot is unloading the part onto the conveyor. Stand back and watch.",
                        "", "The robot moves on its own now; nothing to do until it is done.");
                case GuideStep.Done:
                    return new GuideText("Demo finished",
                        "The demo is finished. You taught the robot to load the machine and to unload the finished part.",
                        "", "");
            }
            return new GuideText(step.ToString(), "", "", "");
        }
    }
}
