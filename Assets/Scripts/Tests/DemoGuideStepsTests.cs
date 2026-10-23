using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Factory;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// The guide derives the next step from the demo's state (never from a counter), so this
    /// walks the happy path forward, then checks the detours: a release outside the vise, a
    /// failed execution, a reposition after the load, doors closed again.
    /// </summary>
    public class DemoGuideStepsTests
    {
        static GuideSignals Confirmed() => new GuideSignals
        {
            welcomeDone = true, placing = false, reachable = 6, targetCount = 6, doorsOpen = true,
            clientPhase = PickPlacePhase.Idle,
        };

        [Test]
        public void FreshScene_StartsWithTheWelcome()
        {
            var s = new GuideSignals { placing = true, targetCount = 6 };
            Assert.AreEqual(GuideStep.Welcome, DemoGuideSteps.Resolve(s));
            s.welcomeDone = true;
            Assert.AreEqual(GuideStep.PlaceRobot, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void Placement_DragUntilReachable_ThenConfirm()
        {
            var s = new GuideSignals { welcomeDone = true, placing = true, reachable = 2, targetCount = 6 };
            Assert.AreEqual(GuideStep.PlaceRobot, DemoGuideSteps.Resolve(s));
            s.reachable = 6;
            s.handleGrabbed = true;
            Assert.AreEqual(GuideStep.PlaceRobot, DemoGuideSteps.Resolve(s), "still in the hand: let go first");
            s.handleGrabbed = false;
            Assert.AreEqual(GuideStep.ConfirmPlacement, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void AfterConfirm_DoorsThenBillet()
        {
            var s = Confirmed();
            s.doorsOpen = false;
            Assert.AreEqual(GuideStep.OpenDoors, DemoGuideSteps.Resolve(s));
            s.doorsMoving = true;
            Assert.AreEqual(GuideStep.OpenDoors, DemoGuideSteps.Resolve(s));
            s.doorsOpen = true; s.doorsMoving = false;
            Assert.AreEqual(GuideStep.SelectBillet, DemoGuideSteps.Resolve(s));
            s.billetSelected = true;
            Assert.AreEqual(GuideStep.GrabBillet, DemoGuideSteps.Resolve(s));
            s.billetGrabbed = true; s.recording = true;
            Assert.AreEqual(GuideStep.CarryToVise, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void ReleasedOutsideTheVise_KeepsAskingForTheVise()
        {
            var s = Confirmed();
            s.billetSelected = true; s.recording = true; s.billetGrabbed = false; s.billetInVise = false;
            Assert.AreEqual(GuideStep.CarryToVise, DemoGuideSteps.Resolve(s));
            s.billetInVise = true;
            Assert.AreEqual(GuideStep.ReleaseInVise, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void PlannerAndRobot_Planning_Execute_Executing()
        {
            var s = Confirmed();
            s.billetSelected = true;
            s.clientPhase = PickPlacePhase.Planning;
            Assert.AreEqual(GuideStep.Planning, DemoGuideSteps.Resolve(s));
            s.clientPhase = PickPlacePhase.Idle; s.solutionCount = 3;
            Assert.AreEqual(GuideStep.Execute, DemoGuideSteps.Resolve(s));
            s.clientPhase = PickPlacePhase.Executing;
            Assert.AreEqual(GuideStep.Executing, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void FailedExecution_LeavesTheSolutions_SoExecuteIsOfferedAgain()
        {
            var s = Confirmed();
            s.solutionCount = 3; s.clientPhase = PickPlacePhase.Idle; s.loadedByRobot = false;
            Assert.AreEqual(GuideStep.Execute, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void Loaded_Clamp_CloseDoors_Done()
        {
            var s = Confirmed();
            s.loadedByRobot = true; s.solutionCount = 3; s.partSettling = true; s.billetInVise = false;
            Assert.AreEqual(GuideStep.Executing, DemoGuideSteps.Resolve(s), "still falling");
            s.partSettling = false; s.billetInVise = true;
            Assert.AreEqual(GuideStep.Clamp, DemoGuideSteps.Resolve(s));
            s.robotInJaws = true;
            Assert.AreEqual(GuideStep.Clamp, DemoGuideSteps.Resolve(s), "clamp step waits for the robot to clear");
            s.robotInJaws = false; s.viseClosed = true;
            Assert.AreEqual(GuideStep.CloseDoors, DemoGuideSteps.Resolve(s));
            s.doorsOpen = false; s.doorsMoving = true;
            Assert.AreEqual(GuideStep.CloseDoors, DemoGuideSteps.Resolve(s));
            s.doorsMoving = false;
            Assert.AreEqual(GuideStep.Machining, DemoGuideSteps.Resolve(s), "doors closed on the clamped part: the job runs");
        }

        [Test]
        public void Unload_Doors_Release_Billet_Conveyor_Plan_Execute_Done()
        {
            var s = Confirmed();
            s.loadedByRobot = true; s.billetInVise = true; s.viseClosed = true; s.doorsOpen = false; s.machined = true;
            Assert.AreEqual(GuideStep.OpenDoorsToUnload, DemoGuideSteps.Resolve(s));
            s.doorsOpen = true;
            Assert.AreEqual(GuideStep.ReleaseVise, DemoGuideSteps.Resolve(s));
            s.viseClosed = false;
            Assert.AreEqual(GuideStep.SelectMachinedBillet, DemoGuideSteps.Resolve(s));
            s.billetSelected = true;
            Assert.AreEqual(GuideStep.GrabMachinedBillet, DemoGuideSteps.Resolve(s));
            s.billetGrabbed = true; s.recording = true;
            Assert.AreEqual(GuideStep.CarryToConveyor, DemoGuideSteps.Resolve(s));
            s.billetGrabbed = false; s.billetInVise = false;
            Assert.AreEqual(GuideStep.CarryToConveyor, DemoGuideSteps.Resolve(s), "released outside the belt zone");
            s.billetOnConveyor = true;
            Assert.AreEqual(GuideStep.ReleaseOnConveyor, DemoGuideSteps.Resolve(s));
            s.recording = false; s.clientPhase = PickPlacePhase.Planning;
            Assert.AreEqual(GuideStep.PlanningUnload, DemoGuideSteps.Resolve(s));
            s.clientPhase = PickPlacePhase.Idle; s.solutionCount = 2;
            Assert.AreEqual(GuideStep.ExecuteUnload, DemoGuideSteps.Resolve(s));
            s.clientPhase = PickPlacePhase.Executing;
            Assert.AreEqual(GuideStep.ExecutingUnload, DemoGuideSteps.Resolve(s));
            s.clientPhase = PickPlacePhase.Idle; s.solutionCount = 0; s.partSettling = true;
            Assert.AreEqual(GuideStep.ExecutingUnload, DemoGuideSteps.Resolve(s), "still falling onto the belt");
            s.partSettling = false; s.unloaded = true;
            Assert.AreEqual(GuideStep.Done, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void UnloadPhase_IgnoresTheLoadsLeftoverState()
        {
            // The director zeroes solutionCount until the unload's own plan; loadedByRobot and
            // billetInVise from the load must not pull the resolver back into the clamp branch.
            var s = Confirmed();
            s.machined = true; s.loadedByRobot = true; s.billetInVise = true; s.viseClosed = false; s.solutionCount = 0;
            Assert.AreEqual(GuideStep.SelectMachinedBillet, DemoGuideSteps.Resolve(s));
            Assert.IsTrue(DemoGuideSteps.IsUnload(GuideStep.SelectMachinedBillet));
            Assert.IsFalse(DemoGuideSteps.IsUnload(GuideStep.Machining));
        }

        [Test]
        public void LoadedButBesideTheVise_FallsBackToTheBillet()
        {
            // DemoGuide clears loadedByRobot when the part settles outside the jaws; the resolver
            // then sees an ordinary "nothing loaded" state with the old solutions still listed.
            var s = Confirmed();
            s.loadedByRobot = false; s.solutionCount = 3; s.billetInVise = false;
            Assert.AreEqual(GuideStep.Execute, DemoGuideSteps.Resolve(s));
            s.solutionCount = 0; s.billetSelected = false;
            Assert.AreEqual(GuideStep.SelectBillet, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void RepositionAfterLoad_GoesBackToPlacement()
        {
            var s = Confirmed();
            s.loadedByRobot = true; s.billetInVise = true; s.placing = true; s.reachable = 0;
            Assert.AreEqual(GuideStep.PlaceRobot, DemoGuideSteps.Resolve(s));
        }

        [Test]
        public void StepNumbers_NeverGoBackwardsOnTheHappyPath()
        {
            var path = new[]
            {
                GuideStep.Welcome, GuideStep.PlaceRobot, GuideStep.ConfirmPlacement, GuideStep.OpenDoors, GuideStep.SelectBillet,
                GuideStep.GrabBillet, GuideStep.CarryToVise, GuideStep.ReleaseInVise, GuideStep.Planning, GuideStep.Execute,
                GuideStep.Executing, GuideStep.Clamp, GuideStep.CloseDoors, GuideStep.Machining, GuideStep.OpenDoorsToUnload,
                GuideStep.ReleaseVise, GuideStep.SelectMachinedBillet, GuideStep.GrabMachinedBillet, GuideStep.CarryToConveyor,
                GuideStep.ReleaseOnConveyor, GuideStep.PlanningUnload, GuideStep.ExecuteUnload, GuideStep.ExecutingUnload,
            };
            int last = 0;
            foreach (GuideStep step in path)
            {
                int n = DemoGuideSteps.NumberOf(step);
                Assert.GreaterOrEqual(n, last, step.ToString());
                Assert.LessOrEqual(n, DemoGuideSteps.StepCount, step.ToString());
                last = n;
            }
            Assert.AreEqual(DemoGuideSteps.StepCount, DemoGuideSteps.NumberOf(GuideStep.ExecutingUnload));
            Assert.AreEqual(0, DemoGuideSteps.NumberOf(GuideStep.Done));
        }

        [Test]
        public void EveryStep_HasAnInstruction_AndButtonStepsHaveATarget()
        {
            var s = Confirmed();
            foreach (GuideStep step in Enum.GetValues(typeof(GuideStep)))
            {
                foreach (bool online in new[] { true, false })
                {
                    GuideText t = DemoGuideSteps.Describe(step, s, online);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(t.title), $"{step} title");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(t.instruction), $"{step} instruction");
                }
            }
            var buttonSteps = new Dictionary<GuideStep, GuideTarget>
            {
                [GuideStep.ConfirmPlacement] = GuideTarget.ConfirmButton,
                [GuideStep.OpenDoors] = GuideTarget.DoorOpenButton,
                [GuideStep.Execute] = GuideTarget.ExecuteButton,
                [GuideStep.Clamp] = GuideTarget.ClampButton,
                [GuideStep.CloseDoors] = GuideTarget.DoorCloseButton,
                [GuideStep.OpenDoorsToUnload] = GuideTarget.DoorOpenButton,
                [GuideStep.ReleaseVise] = GuideTarget.ReleaseButton,
                [GuideStep.ExecuteUnload] = GuideTarget.ExecuteButton,
            };
            foreach (var kv in buttonSteps) Assert.AreEqual(kv.Value, DemoGuideSteps.TargetOf(kv.Key), kv.Key.ToString());
            Assert.AreEqual(GuideTarget.BeginButton, DemoGuideSteps.TargetOf(GuideStep.Welcome));
            Assert.AreEqual(GuideTarget.PlacementHandle, DemoGuideSteps.TargetOf(GuideStep.PlaceRobot));
            Assert.AreEqual(GuideTarget.Billet, DemoGuideSteps.TargetOf(GuideStep.SelectBillet));
            Assert.AreEqual(GuideTarget.ViseWindow, DemoGuideSteps.TargetOf(GuideStep.CarryToVise));
            Assert.AreEqual(GuideTarget.MachinedBillet, DemoGuideSteps.TargetOf(GuideStep.SelectMachinedBillet));
            Assert.AreEqual(GuideTarget.Conveyor, DemoGuideSteps.TargetOf(GuideStep.CarryToConveyor));
            Assert.AreEqual(GuideTarget.None, DemoGuideSteps.TargetOf(GuideStep.Done));
            Assert.AreEqual(GuideTarget.None, DemoGuideSteps.TargetOf(GuideStep.Machining));
            StringAssert.Contains("finished", DemoGuideSteps.Describe(GuideStep.Done, s, true).instruction);
            Assert.IsTrue(new[] { GuideStep.Planning, GuideStep.Executing, GuideStep.ReleaseInVise, GuideStep.Machining, GuideStep.PlanningUnload, GuideStep.ExecutingUnload, GuideStep.ReleaseOnConveyor }.All(DemoGuideSteps.IsWaiting));
            Assert.IsFalse(DemoGuideSteps.IsWaiting(GuideStep.Execute));
        }

        [Test]
        public void TextVariants_FollowTheSignals()
        {
            var s = Confirmed();
            s.placing = true; s.handleGrabbed = true; s.reachable = 4;
            StringAssert.Contains("4 of 6", DemoGuideSteps.Describe(GuideStep.PlaceRobot, s, true).instruction);
            s.handleGrabbed = false;
            StringAssert.Contains("ring", DemoGuideSteps.Describe(GuideStep.PlaceRobot, s, true).instruction);
            var r = Confirmed(); r.recording = true; r.billetGrabbed = false;
            StringAssert.Contains("Not there yet", DemoGuideSteps.Describe(GuideStep.CarryToVise, r, true).instruction);
            r.billetGrabbed = true;
            StringAssert.DoesNotContain("Not there yet", DemoGuideSteps.Describe(GuideStep.CarryToVise, r, true).instruction);
            StringAssert.Contains("connection", DemoGuideSteps.Describe(GuideStep.Planning, r, false).instruction);
            var c = Confirmed(); c.robotInJaws = true;
            StringAssert.Contains("clear", DemoGuideSteps.Describe(GuideStep.Clamp, c, true).instruction);
        }
    }
}
