using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using TMPro;
using Erupt.Interaction;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using Erupt.Ui;

namespace Erupt.Plugins.Ar.Tests
{
    /// <summary>
    /// Placing robots at markers on a fake bus, without a camera: tags are injected into the
    /// tracker. Zero, one and three robots; one tag per robot and one shared work-cell tag; the
    /// proposal and its ghost; the preview button and when it appears; confirming, the refusals and undo.
    /// </summary>
    public class MarkerPlacementTests
    {
        private FakeRosBus bus;
        private RobotRegistry robots;
        private FakeUiHost ui;
        private UndoStack undo;
        private EruptContext context;
        private GameObject pluginGo, routerGo;
        private InteractionRouter router;
        private ArPlugin plugin;
        private AprilTagTracker tracker;
        private float now;
        private readonly List<InteractionRefusal> refusals = new();
        private readonly List<Object> made = new();

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            ui = new FakeUiHost();
            undo = new UndoStack();

            // A router without input sources says so; these tests only use its refusal channel.
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            refusals.Clear();
            router.Refused += refusals.Add;

            // As the package's prefab: the plugin and the tracker on one object. No camera anywhere.
            pluginGo = new GameObject("ar");
            tracker = pluginGo.AddComponent<AprilTagTracker>();
            tracker.PoseFilterCoefficient = 0f;
            now = 50f;
            tracker.clock = () => now;
            plugin = pluginGo.AddComponent<ArPlugin>();
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin != null && plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            GameObject ghost;
            while ((ghost = GameObject.Find("PlacementGhost")) != null) Object.DestroyImmediate(ghost);
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(routerGo);
            FakeRobots.DestroyAll(robots);
            foreach (Object o in made)
                if (o != null) Object.DestroyImmediate(o);
            made.Clear();
        }

        private void Register(RobotRegistry registry)
        {
            robots = registry;
            context = new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = undo, Router = router };
            ((IEruptPlugin)plugin).OnRegister(context);
        }

        private static MarkerAnchor Anchor(RobotInstance robot, int tagId, Vector3 position, float yaw = 0f, float size = 0.10f)
        {
            var go = new GameObject("MarkerAnchor");
            go.transform.SetParent(robot.transform, false);
            var anchor = go.AddComponent<MarkerAnchor>();
            anchor.Configure(tagId, size, position, new Vector3(0f, yaw, 0f));
            return anchor;
        }

        private void See(int tagId, Vector3 position, float heading = 0f)
        {
            plugin.Refresh();   // the plugin has the tracker look for the anchors' tags
            Assert.IsTrue(tracker.InjectObservation(tagId, MarkerMathTests.FlatTag(position, heading)), $"tag {tagId} is not looked for");
            plugin.Refresh();
        }

        private Button ButtonFor(RobotInstance robot) =>
            ui.Widgets.SingleOrDefault(widget => widget.Id == $"ar.place.{robot.Id}")?.GameObject.GetComponentInChildren<Button>();

        private static Pose PoseOf(RobotInstance robot) => robot.BasePose;

        // A profile whose ghost prefab is a cube, so the robot can show where it would go.
        private RobotProfile ProfileWithGhost()
        {
            var source = new GameObject("ghost source");
            made.Add(source);
            GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(source.transform, false);
            var profile = ScriptableObject.CreateInstance<RobotProfile>();
            made.Add(profile);
            profile.ghostPrefab = source;
            return profile;
        }

        // ---------- registration: zero, one, three robots ----------

        [Test]
        public void WithNoRobot_ThePluginRegisters_ContributesItsTab_AndHasNothingToPlace()
        {
            Register(FakeRobots.Registry(bus));

            Assert.AreEqual("ar", plugin.Id);
            Assert.AreEqual(0, plugin.Sessions.Count);
            CollectionAssert.Contains(ui.Tabs, ArPlugin.TabId);
            Assert.IsEmpty(ui.Registered, "Placement adds no verb to the solid robot.");
            Assert.IsEmpty(ui.Widgets);
            Assert.AreSame(tracker, plugin.Tracker, "The tracker next to the plugin is the one it reads.");
            plugin.Refresh();
            StringAssert.Contains("no passthrough camera", plugin.StatusText());
            Assert.IsTrue(plugin.PlaceAtMarker(null).IsRefused);
            Assert.IsEmpty(bus.Publishes, "The plugin itself says nothing on ROS.");
            Assert.IsEmpty(bus.Subscriptions);
        }

        [Test]
        public void WithOneRobot_ItsTagComesIntoView_TheProposalIsShown_AndConfirmingPlacesIt()
        {
            RobotRegistry registry = FakeRobots.Single(bus, new FakeRobotModel("j1"), profile: ProfileWithGhost());
            RobotInstance robot = registry.All[0];
            RobotGhosts ghosts = robot.gameObject.AddComponent<RobotGhosts>();
            // The base stands 40 cm behind the tag (away from its top edge), facing the top edge.
            Anchor(robot, 0, new Vector3(0f, 0f, -0.4f));
            Register(registry);
            plugin.Refresh();

            // Before the tag is seen: no proposal, no verb, nothing shown.
            Assert.IsFalse(plugin.SessionFor(robot).HasProposal);
            Assert.IsNull(ButtonFor(robot));
            Assert.IsNull(ghosts.PlacementGhost);

            // The tag lies on a table at (2, 0.8, 1), top edge along world x.
            See(0, new Vector3(2f, 0.8f, 1f), 90f);
            var expected = new Pose(new Vector3(1.6f, 0.8f, 1f), Quaternion.Euler(0f, 90f, 0f));

            ArPlugin.RobotSession session = plugin.SessionFor(robot);
            Assert.IsTrue(session.HasProposal);
            MarkerMathTests.AssertPose(expected, session.Proposal);
            Assert.IsTrue(session.ShowsProposal);
            Assert.IsNotNull(ghosts.PlacementGhost, "A translucent copy shows where the robot would go.");
            MarkerMathTests.AssertPose(expected, new Pose(ghosts.PlacementGhost.transform.position, ghosts.PlacementGhost.transform.rotation));
            Assert.AreEqual("Place here", ButtonFor(robot).GetComponentInChildren<TextMeshProUGUI>().text);
            Assert.AreEqual(Vector3.zero, robot.transform.position, "Nothing moves until the user confirms.");

            int moved = 0;
            robot.BaseMoved += (_, _) => moved++;
            ButtonFor(robot).onClick.Invoke();

            MarkerMathTests.AssertPose(expected, PoseOf(robot));
            Assert.AreEqual(1, moved);
            Assert.IsEmpty(refusals);
            Assert.IsFalse(session.ShowsProposal, "The robot stands where its tag puts it: no copy on top of it.");
            Assert.IsNull(ghosts.PlacementGhost);
            Assert.IsNull(ButtonFor(robot), "The button disappears when the preview is accepted.");
        }

        [Test]
        public void APlacedRobot_StaysWhereItIs_WhenItsTagGoesOutOfView_OrMoves()
        {
            RobotRegistry registry = FakeRobots.Single(bus, new FakeRobotModel("j1"));
            RobotInstance robot = registry.All[0];
            Anchor(robot, 0, Vector3.zero);
            Register(registry);
            See(0, new Vector3(1f, 0f, 1f));
            ButtonFor(robot).onClick.Invoke();
            var placed = new Pose(new Vector3(1f, 0f, 1f), Quaternion.identity);
            MarkerMathTests.AssertPose(placed, PoseOf(robot));

            // The tag is picked up and carried about: the robot is not dragged along.
            See(0, new Vector3(3f, 0f, -2f), 45f);
            MarkerMathTests.AssertPose(placed, PoseOf(robot));
            Assert.IsTrue(plugin.SessionFor(robot).HasProposal, "It is proposed again, and no more than that.");

            // Then it is out of view.
            now += 5f;
            plugin.Refresh();
            MarkerMathTests.AssertPose(placed, PoseOf(robot));
            Assert.IsFalse(plugin.SessionFor(robot).HasProposal);
            Assert.IsNull(ButtonFor(robot), "A tag that is not in view offers nothing.");
        }

        [Test]
        public void WithThreeRobots_EachOnItsOwnTag_OnlyTheRobotWhoseTagIsSeenIsOfferedAndPlaced()
        {
            Register(FakeRobots.Three(bus));
            RobotInstance left = robots.All[0], right = robots.All[1], third = robots.All[2];
            Anchor(left, 10, Vector3.zero);
            Anchor(right, 11, new Vector3(0.2f, 0f, 0f), 180f);
            // third has no marker anchor: it is not bound to a tag.
            plugin.Refresh();
            Assert.AreEqual(3, plugin.Sessions.Count);

            See(11, new Vector3(0f, 1f, 4f));

            Assert.IsNotNull(ButtonFor(right));
            Assert.IsNull(ButtonFor(left), "Its own tag has not been seen.");
            Assert.IsNull(ButtonFor(third), "No marker anchor.");
            Assert.AreEqual("Place right here", ButtonFor(right).GetComponentInChildren<TextMeshProUGUI>().text);

            ButtonFor(right).onClick.Invoke();

            MarkerMathTests.AssertPose(new Pose(new Vector3(0.2f, 1f, 4f), Quaternion.Euler(0f, 180f, 0f)), PoseOf(right));
            Assert.AreEqual(Vector3.zero, left.transform.position);
            Assert.AreEqual(Vector3.zero, third.transform.position);
            Assert.AreEqual("Place right", undo.NextUndoLabel);
        }

        [Test]
        public void WithThreeRobots_OnOneSharedWorkCellTag_EachGoesToItsOwnOffset()
        {
            Register(FakeRobots.Three(bus));
            RobotInstance left = robots.All[0], right = robots.All[1], third = robots.All[2];
            // One tag in the middle of the cell; the robots stand around it.
            Anchor(left, 5, new Vector3(-0.6f, 0f, 0f));
            Anchor(right, 5, new Vector3(0.6f, 0f, 0f));
            Anchor(third, 5, new Vector3(0f, 0f, 1.2f), 180f);
            plugin.Refresh();

            See(5, new Vector3(1f, 0.75f, 2f));

            foreach (RobotInstance robot in robots.All) Assert.IsNotNull(ButtonFor(robot), robot.Id);
            MarkerMathTests.AssertPose(new Pose(new Vector3(0.4f, 0.75f, 2f), Quaternion.identity), plugin.SessionFor(left).Proposal);
            MarkerMathTests.AssertPose(new Pose(new Vector3(1.6f, 0.75f, 2f), Quaternion.identity), plugin.SessionFor(right).Proposal);
            MarkerMathTests.AssertPose(new Pose(new Vector3(1f, 0.75f, 3.2f), Quaternion.Euler(0f, 180f, 0f)), plugin.SessionFor(third).Proposal);

            // Each is confirmed on its own; confirming one moves no other.
            ButtonFor(third).onClick.Invoke();
            MarkerMathTests.AssertPose(new Pose(new Vector3(1f, 0.75f, 3.2f), Quaternion.Euler(0f, 180f, 0f)), PoseOf(third));
            Assert.AreEqual(Vector3.zero, left.transform.position);
            Assert.AreEqual(Vector3.zero, right.transform.position);

            ButtonFor(left).onClick.Invoke();
            ButtonFor(right).onClick.Invoke();
            Assert.Less(Vector3.Distance(new Vector3(0.4f, 0.75f, 2f), left.transform.position), 1e-4f);
            Assert.Less(Vector3.Distance(new Vector3(1.6f, 0.75f, 2f), right.transform.position), 1e-4f);
            Assert.IsEmpty(refusals);
        }

        [UnityTest]
        public IEnumerator ARobotThatJoinsLater_IsServed_AndOneThatLeaves_TakesItsGhostWithIt()
        {
            Register(FakeRobots.Registry(bus));
            RobotInstance late = FakeRobots.Add(robots, "late", "late", new FakeRobotModel("j1"), ProfileWithGhost());
            RobotGhosts ghosts = late.gameObject.AddComponent<RobotGhosts>();
            Anchor(late, 2, Vector3.zero);
            Assert.AreEqual(1, plugin.Sessions.Count);

            See(2, new Vector3(1f, 0f, 0f));
            Assert.IsNotNull(ghosts.PlacementGhost);
            GameObject ghost = ghosts.PlacementGhost;
            Assert.AreSame(late, robots.OwnerOf(ghost));

            Object.DestroyImmediate(late.gameObject);
            Assert.AreEqual(0, plugin.Sessions.Count);
            Assert.IsEmpty(ui.Widgets);
            yield return null;
            Assert.IsTrue(ghost == null, "The copy does not outlive its robot.");
        }

        [Test]
        public void TheAnchor_MayHoldTheRobot_InsteadOfSittingUnderIt()
        {
            // As in the AR sample scene: an empty "robot slot" carries the anchor, and a robot prefab is dropped onto it.
            var slot = new GameObject("Robot Slot");
            made.Add(slot);
            var anchor = slot.AddComponent<MarkerAnchor>();
            anchor.Configure(0, 0.10f, new Vector3(0f, 0f, 0.3f), Vector3.zero);
            RobotRegistry registry = FakeRobots.Single(bus, new FakeRobotModel("j1"));
            RobotInstance robot = registry.All[0];
            robot.transform.SetParent(slot.transform, false);
            Register(registry);

            Assert.AreSame(robot, anchor.Robot);
            Assert.AreSame(anchor, MarkerAnchor.Of(robot));
            See(0, new Vector3(2f, 0f, 2f));
            Assert.IsFalse(plugin.PlaceAtMarker(robot).IsRefused);
            Assert.Less(Vector3.Distance(new Vector3(2f, 0f, 2.3f), robot.transform.position), 1e-4f);
            Assert.AreEqual(Vector3.zero, slot.transform.position, "The robot is moved, not the slot.");
        }

        // ---------- refusals ----------

        [Test]
        public void Placing_IsRefused_WithTheReason_WhenThereIsNothingToGoBy()
        {
            Register(FakeRobots.Three(bus));
            RobotInstance left = robots.All[0], right = robots.All[1], third = robots.All[2];
            Anchor(left, 10, Vector3.zero);
            Anchor(right, 11, Vector3.zero);
            plugin.Refresh();

            // No marker anchor.
            InteractionRefusal none = plugin.PlaceAtMarker(third);
            Assert.IsTrue(none.IsRefused);
            Assert.AreEqual(RefusalCode.BaseNotPlaced, none.Code);
            StringAssert.Contains("Robot 'third' has no marker anchor", none.Reason);
            Assert.AreSame(third.transform, none.Subject);

            // Never seen.
            InteractionRefusal unseen = plugin.PlaceAtMarker(left);
            StringAssert.Contains("Tag 10 has not been seen yet", unseen.Reason);

            // Seen, then out of view.
            See(11, new Vector3(1f, 0f, 1f));
            now += 9f;
            plugin.Refresh();
            InteractionRefusal lost = plugin.PlaceAtMarker(right);
            StringAssert.Contains("Tag 11 is not in view (last seen 9 s ago)", lost.Reason);

            foreach (RobotInstance robot in robots.All) Assert.AreEqual(Vector3.zero, robot.transform.position, robot.Id);
            Assert.IsFalse(undo.CanUndo, "A refused placement leaves no trace in the history.");
        }

        [Test]
        public void PressingAfterTrackingExpires_ReportsTheRefusalBesideThePreview()
        {
            RobotRegistry registry = FakeRobots.Single(bus, new FakeRobotModel("j1"), id: "arm");
            Anchor(registry.All[0], 0, Vector3.zero);
            RobotInstance robot = registry.All[0];
            Register(registry);
            See(0, Vector3.right);
            Button button = ButtonFor(robot);
            Vector3 at = button.transform.position;
            now += 5f; // The press can arrive before the next refresh hides the button.
            button.onClick.Invoke();

            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RefusalCode.BaseNotPlaced, refusals[0].Code);
            Assert.AreEqual(at, refusals[0].WorldPoint);
            StringAssert.Contains("not in view", refusals[0].Reason);
            Assert.IsEmpty(ui.Widgets);
            Assert.IsFalse(undo.CanUndo);
        }

        [Test]
        public void SmallJitterLeavesTheButtonStill_AndANewDetectionCannotConfirmAnUnseenPose()
        {
            Register(FakeRobots.Three(bus));
            var robot = robots.All[1];
            Anchor(robot, 3, Vector3.zero);
            See(3, Vector3.right);
            var button = ButtonFor(robot);
            Vector3 at = button.transform.position;
            See(3, Vector3.right * 1.003f);
            Assert.AreEqual(at, button.transform.position);

            tracker.InjectObservation(3, MarkerMathTests.FlatTag(Vector3.right * 2f));
            button.onClick.Invoke();
            Assert.IsFalse(undo.CanUndo);
            Assert.AreEqual(Vector3.zero, robot.BasePose.position);
            Assert.AreEqual(1, refusals.Count);
            Assert.Greater(Vector3.Distance(at, button.transform.position), 0.9f);
            button.onClick.Invoke();
            Assert.AreEqual(Vector3.right * 2f, robot.BasePose.position);
        }

        [Test]
        public void Placing_IsRefused_WhileSomethingNeedsTheBaseWhereItIs()
        {
            RobotRegistry registry = FakeRobots.Single(bus, new FakeRobotModel("j1"), id: "arm");
            RobotInstance robot = registry.All[0];
            Anchor(robot, 0, Vector3.zero);
            Register(registry);
            See(0, new Vector3(1f, 0f, 1f));
            string busy = "Robot 'arm' is executing a trajectory.";
            robot.AddBaseGuard(() => busy);

            InteractionRefusal refused = plugin.PlaceAtMarker(robot);

            Assert.IsTrue(refused.IsRefused);
            Assert.AreEqual("Robot 'arm' is executing a trajectory. It cannot be moved now.", refused.Reason);
            Assert.AreEqual(Vector3.zero, robot.transform.position);
            Assert.IsFalse(undo.CanUndo);

            busy = null;
            Assert.IsFalse(plugin.PlaceAtMarker(robot).IsRefused);
            Assert.Less(Vector3.Distance(new Vector3(1f, 0f, 1f), robot.transform.position), 1e-4f);
        }

        [Test]
        public void OneTag_UsedWithTwoSizes_PlacesTheFirstRobot_AndRefusesTheOther()
        {
            Register(FakeRobots.Three(bus));
            RobotInstance left = robots.All[0], right = robots.All[1];
            Anchor(left, 5, Vector3.zero, size: 0.10f);
            Anchor(right, 5, Vector3.zero, size: 0.05f);

            LogAssert.Expect(LogType.Error, new Regex("Tag 5 is asked for with a size of 0.05 m but is already tracked with 0.1 m"));
            See(5, new Vector3(1f, 0f, 1f));

            Assert.IsTrue(plugin.SessionFor(left).HasProposal);
            Assert.IsFalse(plugin.SessionFor(right).HasProposal);
            InteractionRefusal refused = plugin.PlaceAtMarker(right);
            StringAssert.Contains("Tag 5 is used with two sizes", refused.Reason);
        }

        [Test]
        public void WithoutATracker_PlacingIsRefused_AndThePluginSaysSoOnce()
        {
            Object.DestroyImmediate(tracker);
            RobotRegistry registry = FakeRobots.Single(bus, new FakeRobotModel("j1"));
            Anchor(registry.All[0], 0, Vector3.zero);

            LogAssert.Expect(LogType.Warning, new Regex("No AprilTagTracker in the scene"));
            Register(registry);
            plugin.Refresh();

            StringAssert.Contains("no AprilTag tracker", plugin.PlaceAtMarker(registry.All[0]).Reason);
            StringAssert.Contains("Tracker: none in the scene", plugin.StatusText());
        }

        // ---------- undo ----------

        [Test]
        public void Undo_PutsTheBaseBack_AndRedoPlacesItAgain()
        {
            RobotRegistry registry = FakeRobots.Single(bus, new FakeRobotModel("j1"), profile: ProfileWithGhost());
            RobotInstance robot = registry.All[0];
            RobotGhosts ghosts = robot.gameObject.AddComponent<RobotGhosts>();
            var start = new Pose(new Vector3(-1f, 0f, 0.5f), Quaternion.Euler(0f, 30f, 0f));
            robot.transform.SetPositionAndRotation(start.position, start.rotation);
            Anchor(robot, 0, Vector3.zero);
            Register(registry);
            See(0, new Vector3(1f, 0f, 1f), 90f);
            var placed = new Pose(new Vector3(1f, 0f, 1f), Quaternion.Euler(0f, 90f, 0f));

            Assert.IsFalse(plugin.PlaceAtMarker(robot).IsRefused);
            MarkerMathTests.AssertPose(placed, PoseOf(robot));

            Assert.IsTrue(undo.Undo());
            MarkerMathTests.AssertPose(start, PoseOf(robot));
            plugin.Refresh();
            Assert.IsNotNull(ghosts.PlacementGhost, "Back where it was, the proposal is shown again.");
            Assert.IsNotNull(ButtonFor(robot));

            Assert.IsTrue(undo.Redo());
            MarkerMathTests.AssertPose(placed, PoseOf(robot));
            plugin.Refresh();
            Assert.IsNull(ghosts.PlacementGhost);
            Assert.IsEmpty(ui.Widgets);
        }

        // ---------- status ----------

        [Test]
        public void TheStatus_NamesEveryRobot_ItsTag_AndWhatToDoNext()
        {
            Register(FakeRobots.Three(bus));
            RobotInstance left = robots.All[0], right = robots.All[1];
            Anchor(left, 10, Vector3.zero);
            Anchor(right, 11, Vector3.zero);
            plugin.Refresh();
            See(10, new Vector3(0.5f, 0f, 0f));

            string status = plugin.StatusText();

            StringAssert.Contains("left: tag 10 in view, 50 cm from the robot. Press the placement button beside the proposed base.", status);
            StringAssert.Contains("right: tag 11 not seen yet", status);
            StringAssert.Contains("third: no marker anchor", status);

            plugin.PlaceAtMarker(left);
            StringAssert.Contains("left: tag 10 in view; the robot stands where it puts it.", plugin.StatusText());
            now += 10f;
            plugin.Refresh();
            StringAssert.Contains("left: tag 10 not in view", plugin.StatusText());
        }

        [Test]
        public void Unregistering_RemovesTheGhosts_AndThePlacementWidgets()
        {
            RobotRegistry registry = FakeRobots.Single(bus, new FakeRobotModel("j1"), profile: ProfileWithGhost());
            RobotInstance robot = registry.All[0];
            RobotGhosts ghosts = robot.gameObject.AddComponent<RobotGhosts>();
            Anchor(robot, 0, Vector3.zero);
            Register(registry);
            See(0, new Vector3(1f, 0f, 1f));
            Assert.IsNotNull(ghosts.PlacementGhost);

            ((IEruptPlugin)plugin).OnUnregister(context);

            Assert.IsNull(ghosts.PlacementGhost);
            Assert.IsNull(plugin.Sessions);
            Assert.IsEmpty(ui.Widgets);
            CollectionAssert.DoesNotContain(ui.Tabs, ArPlugin.TabId, "The AR tab goes with the module.");
            Assert.IsTrue(plugin.PlaceAtMarker(robot).IsRefused);
        }
    }
}
