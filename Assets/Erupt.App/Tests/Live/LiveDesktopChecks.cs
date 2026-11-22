using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Interaction.Backends;
using Erupt.Plugins;
using Erupt.Plugins.MoveIt;
using Erupt.Robot;
using RosMessageTypes.Sensor;

namespace Erupt.App.Live.Tests
{
    /// <summary>
    /// The quick start's flow against the real ROS side, with nothing but a mouse and a
    /// keyboard: the input is simulated (the Input System's test fixture), so every click goes
    /// through the scene's event system, the mouse backend, the router and the tier UI, and
    /// what MoveIt and the robot's controller make of it is real.
    /// </summary>
    public abstract partial class LiveSceneFixture
    {
        private sealed class Desk
        {
            public readonly InputTestFixture Input = new InputTestFixture();
            public Mouse Mouse;
            public Keyboard Keyboard;
            public Camera Camera;
            public DesktopControls Controls;

            public Vector2 OnScreen(Vector3 world, string what)
            {
                Vector3 view = Camera.WorldToViewportPoint(world);
                Assert.IsTrue(view.z > Camera.nearClipPlane && view.x > 0.02f && view.x < 0.98f && view.y > 0.02f && view.y < 0.98f,
                    $"{what} is in the desktop view (viewport {view.x:F2}, {view.y:F2}, depth {view.z:F2}).");
                return Camera.WorldToScreenPoint(world);
            }

            public IEnumerator Point(Vector2 screen)
            {
                Input.Move(Mouse.position, screen);
                yield return null;
            }

            public IEnumerator Click(Vector2 screen)
            {
                yield return Point(screen);
                Input.Press(Mouse.leftButton);
                yield return null;
                Input.Release(Mouse.leftButton);
                yield return null;
                yield return null;
            }

            public IEnumerator Tap(Key key)
            {
                Input.Press(Keyboard[key]);
                yield return null;
                Input.Release(Keyboard[key]);
                yield return null;
            }
        }

        private static IEnumerator ClickVerb(Desk desk, string verbId)
        {
            Assert.IsTrue(Ui.TierTwo.Canvas.gameObject.activeInHierarchy, $"The tier 2 menu is showing (for '{verbId}').");
            Button button = Ui.TierTwo.ButtonFor(verbId);
            Assert.IsNotNull(button, $"'{verbId}' is on the menu (it has: {string.Join(", ", Ui.TierTwo.Model.Verbs.Select(v => v.Id))}).");
            Assert.IsTrue(button.interactable, $"'{verbId}' can be pressed.");
            yield return desk.Click(desk.OnScreen(button.transform.position, $"The button '{verbId}'"));
        }

        private static Button PanelButton(string name) =>
            Ui.TierThree.Canvas.GetComponentsInChildren<Button>(false).FirstOrDefault(b => b.name == name);

        /// <summary>
        /// Select the end effector with a click, Set Start, drag the arm with the right button,
        /// Set Goal, Plan, Execute, then the panel from the keyboard: mode, the MoveIt tab, an
        /// obstacle from the Scene tab that reaches MoveIt's planning scene and is deleted again.
        /// </summary>
        protected static IEnumerator AssertDesktopFlow(RobotInstance robot)
        {
            MoveItRobotSession session = MoveIt.SessionFor(robot);
            Assert.IsNotNull(session, $"MoveIt serves '{robot.Id}'.");
            yield return MoveRosRobotTo(robot, robot.Profile.initialPose);
            session.StopPreview();
            yield return SyncFromRos(robot);
            MoveIt.ResetPlanningState(robot);
            Context.Selection.ClearSelection();
            if (Ui.TierThree.IsOpen) Ui.ToggleTierThree();

            var desk = new Desk();
            desk.Input.Setup();
            // See DesktopFlowTests: the fixture's clock must start past the editor's play-mode transition.
            desk.Input.currentTime = 1e6;
            int refusalsBefore = Refusals.Count;
            try
            {
                desk.Mouse = InputSystem.AddDevice<Mouse>();
                desk.Keyboard = InputSystem.AddDevice<Keyboard>();
                desk.Controls = UnityEngine.Object.FindFirstObjectByType<DesktopControls>();
                Assert.IsNotNull(desk.Controls, "The scene's ERUPT root carries the desktop controls.");
                yield return LiveRos.Until(() => desk.Controls.IsActive, 5f, "the desktop controls to take the view (no headset)");
                yield return LiveRos.Seconds(0.6f);   // the view settles on the robot
                desk.Camera = Camera.main;
                Assert.AreSame(desk.Camera, desk.Controls.View);

                // 1. Left click on the end effector.
                Transform handle = robot.Interaction.Handle;
                yield return desk.Click(desk.OnScreen(handle.position, "The end-effector handle"));
                Assert.AreEqual(SelectionKind.EndEffector, Context.Selection.CurrentKind, "A left click on the handle selects the end effector.");

                // 2. Set Start.
                yield return ClickVerb(desk, "set-start");
                Assert.IsTrue(session.HasStart, "A click on Set Start captured the start.");

                // 3. Right-drag: the arm follows.
                Vector3 before = robot.Model.EndEffector.position;
                Vector2 from = desk.OnScreen(handle.position, "The end-effector handle");
                Vector2 to = from + new Vector2(-0.09f, 0.06f) * desk.Camera.pixelHeight;
                yield return desk.Point(from);
                desk.Input.Press(desk.Mouse.rightButton);
                yield return null;
                for (int i = 1; i <= 15; i++)
                {
                    desk.Input.Move(desk.Mouse.position, Vector2.Lerp(from, to, i / 15f));
                    yield return LiveRos.Seconds(0.04f);
                }
                yield return LiveRos.Seconds(1.0f);
                desk.Input.Release(desk.Mouse.rightButton);
                yield return null;
                yield return null;
                Assert.Greater(Vector3.Distance(before, robot.Model.EndEffector.position), 0.04f, "The end effector followed the right-drag.");
                Assert.AreEqual(SelectionKind.EndEffector, Context.Selection.CurrentKind, "After the drag the end effector is the selection: its menu is there.");
                Dictionary<string, float> goal = UnityJoints(robot);

                // 4. Set Goal, Plan: MoveIt answers, and the plan is the selection.
                yield return ClickVerb(desk, "set-goal");
                Assert.IsTrue(session.HasGoal, "A click on Set Goal captured the goal.");
                var outcome = new PlanOutcome();
                Action<PlanResult> produced = r => { if (r.RobotId == robot.Id) outcome.Result = r; };
                Action<string> failed = m => outcome.Failure = m ?? "(no message)";
                MoveIt.PlanProduced += produced;
                MoveIt.PlanFailed += failed;
                try
                {
                    yield return ClickVerb(desk, "plan");
                    yield return LiveRos.Until(() => outcome.Done, 60f, $"MoveIt's answer to the plan request of '{robot.Id}'");
                }
                finally
                {
                    MoveIt.PlanProduced -= produced;
                    MoveIt.PlanFailed -= failed;
                }
                Assert.IsNull(outcome.Failure, $"MoveIt failed to plan for '{robot.Id}': {outcome.Failure}");
                yield return null;
                yield return null;
                Assert.AreEqual(SelectionKind.Trajectory, Context.Selection.CurrentKind, "The new plan is the selection.");

                // 5. Execute: the ROS robot goes to the goal and the Unity robot follows.
                string[] rosNames = robot.Names.ToRos(outcome.Result.Trajectory.joint_names);
                double[] last = outcome.Result.Trajectory.points[^1].positions;
                using (var states = new LiveRos.TopicCounter<JointStateMsg>(robot.Ros, robot.JointStatesTopic))
                {
                    yield return ClickVerb(desk, "execute");
                    bool timedOut = false;
                    yield return LiveRos.Until(() => states.Count > 0 && Farthest(states.Messages[^1], rosNames, last) < 0.01, 40f, t => timedOut = t);
                    Assert.IsFalse(timedOut, $"After a click on Execute the ROS robot '{robot.Id}' did not reach the goal ({states.Count} joint states seen).");
                    yield return LiveRos.Seconds(0.5f);
                    JointStateMsg final = states.Messages[^1];
                    Dictionary<string, float> unity = UnityJoints(robot);
                    for (int i = 0; i < final.name.Length; i++)
                        Assert.AreEqual(final.position[i], unity[final.name[i]], JointTolerance, $"After the execution, '{robot.Id}' {final.name[i]}: Unity vs joint_states.");
                    foreach (string joint in rosNames)
                        Assert.AreEqual(goal[joint], unity[joint], 0.02f, $"'{robot.Id}' {joint} is where the user dragged it.");
                }
                yield return LiveRos.Until(() => !session.Client.IsFollowingExecution, 20f, $"the execution on '{robot.Id}' to be reported finished");

                // 6. The panel from the keyboard and from tier 1's Menu button; the MoveIt and the Scene tab.
                yield return desk.Tap(desk.Controls.MouseBackend.PanelKey);
                Assert.IsTrue(Ui.TierThree.IsOpen, "The panel key opens tier 3.");
                Button menu = Ui.TierOne.Canvas.GetComponentsInChildren<Button>().First(b => b.name == "Menu");
                yield return desk.Click(desk.OnScreen(menu.transform.position, "The Menu button"));
                Assert.IsFalse(Ui.TierThree.IsOpen, "A click on tier 1's Menu button closes the panel.");
                yield return desk.Click(desk.OnScreen(menu.transform.position, "The Menu button"));
                Assert.IsTrue(Ui.TierThree.IsOpen, "And another opens it again.");

                Button moveItTab = PanelButton($"Tab_{MoveIt.SettingsTabId}");
                if (moveItTab.interactable) yield return desk.Click(desk.OnScreen(moveItTab.transform.position, "The MoveIt tab"));
                Assert.AreEqual(MoveIt.SettingsTabId, Ui.TierThree.ActiveTab.Id);
                Assert.IsFalse(session.Client.IsMirroring);
                yield return desk.Click(desk.OnScreen(PanelButton("Mirror").transform.position, "Mirror joints"));
                Assert.IsTrue(session.Client.IsMirroring, "A click on Mirror joints switched the mirror on.");
                yield return desk.Click(desk.OnScreen(PanelButton("Mirror").transform.position, "Mirror joints"));
                Assert.IsFalse(session.Client.IsMirroring);

                yield return desk.Click(desk.OnScreen(PanelButton("Tab_scene").transform.position, "The Scene tab"));
                var known = new HashSet<EnvironmentObject>(Context.Environment.All);
                yield return desk.Click(desk.OnScreen(PanelButton("Cube").transform.position, "Add Cube"));
                EnvironmentObject cube = Context.Environment.All.FirstOrDefault(o => !known.Contains(o));
                Assert.IsNotNull(cube, "A click on Add Cube added an obstacle.");
                Spawned.Add(cube.gameObject);
                yield return desk.Tap(desk.Controls.MouseBackend.PanelKey);
                Assert.IsFalse(Ui.TierThree.IsOpen);
                string cubeId = cube.Id;
                yield return AssertInPlanningScene(robot, cube.gameObject, cubeId);

                // The new obstacle is the selection; its Delete button takes it out of MoveIt again.
                Assert.AreSame(cube.gameObject, Context.Selection.Current?.GameObject, "The new obstacle is selected.");
                yield return ClickVerb(desk, "delete");
                yield return AssertNotInPlanningScene(robot, cubeId);

                List<InteractionRefusal> refused = Refusals.Skip(refusalsBefore)
                    .Where(r => r.Code != RefusalCode.JointLimit && r.Code != RefusalCode.OutOfReach).ToList();
                Assert.IsEmpty(refused, "Something in the flow was refused: " + string.Join("; ", refused.Select(r => $"{r.Code}: {r.Reason}")));
            }
            finally
            {
                desk.Input.TearDown();
            }
            yield return null;
            MoveIt.StopPreview(robot);
            MoveIt.ResetPlanningState(robot);
            Context.Selection.ClearSelection();
            yield return SyncFromRos(robot);
            AssertNoExceptions();
        }
    }
}
