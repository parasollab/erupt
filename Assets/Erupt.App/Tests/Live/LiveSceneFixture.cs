using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Unity.Robotics.ROSTCPConnector;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Demonstration;
using Erupt.Plugins.MoveIt;
using Erupt.Plugins.Mtc;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Ui;

namespace Erupt.App.Live.Tests
{
    /// <summary>
    /// Base of the live fixtures: loads one of the reference app's scenes once, on the real
    /// connection, and keeps it for every test of the fixture (the ROS side keeps its state
    /// between tests too, so the tests of a fixture are one ordered session, like a user's).
    /// </summary>
    public abstract partial class LiveSceneFixture
    {
        /// <summary>The value of <see cref="LiveRos.ScenarioVariable"/> this fixture runs under.</summary>
        protected abstract string Scenario { get; }
        /// <summary>The scene to load (by name; it is in the build settings).</summary>
        protected abstract string EntryScene { get; }
        /// <summary>The scene the app ends up in (the entry scene may hand over to another).</summary>
        protected abstract string MainScene { get; }
        /// <summary>False for a fixture that starts with nothing listening on the endpoint.</summary>
        protected virtual bool ExpectsRos => true;

        private static string loadedScene;
        private static bool logging;

        /// <summary>The app's connection in the current scene (it is replaced when the scene is reloaded).</summary>
        protected static ROSConnection Connection => LiveRos.CurrentConnection;
        protected static PluginHost Host { get; private set; }
        protected static IEruptContext Context => Host != null ? Host.Context : null;
        protected static MoveItPlugin MoveIt { get; private set; }
        protected static MtcPlugin Mtc { get; private set; }
        protected static DemonstrationCollectionPlugin Demonstrations { get; private set; }
        protected static TierUiRig Ui { get; private set; }

        /// <summary>Every error, assertion and exception the app logged since the scene was loaded.</summary>
        protected static readonly List<string> Errors = new();
        /// <summary>Every warning the app logged since the scene was loaded.</summary>
        protected static readonly List<string> Warnings = new();
        /// <summary>Every exception the app logged since the scene was loaded.</summary>
        protected static readonly List<string> Exceptions = new();
        /// <summary>Every refusal the interaction router raised since the scene was loaded.</summary>
        protected static readonly List<InteractionRefusal> Refusals = new();

        protected static IReadOnlyList<RobotInstance> Robots => Context.Robots.All;

        protected static RobotInstance Robot(string id)
        {
            Assert.IsTrue(Context.Robots.TryGet(id, out RobotInstance robot), $"No robot '{id}' in the scene.");
            return robot;
        }

        /// <summary>
        /// First line of every test: ignores the test unless the suite is switched on for this
        /// scenario, and loads the scene when it is not up yet.
        /// </summary>
        protected IEnumerator Enter()
        {
            LiveRos.Require(Scenario);
            // The scenes carry the XR rig, which logs errors about missing devices when run
            // headless. The tests assert on Errors / Exceptions themselves. (The flag belongs to
            // the running test's log scope, so it is set here and not in a SetUp.)
            LogAssert.ignoreFailingMessages = true;
            if (loadedScene == MainScene)
            {
                CleanUpAfterEarlierTest();
                yield break;
            }

            if (!logging)
            {
                logging = true;
                Application.logMessageReceived += OnLog;
            }
            RosBus.Reset();
            LiveRos.UseEndpointIn(MainScene);

            yield return SceneManager.LoadSceneAsync(EntryScene, LoadSceneMode.Single);
            yield return LiveRos.Until(() => SceneManager.GetActiveScene().name == MainScene && SceneManager.GetActiveScene().isLoaded,
                60f, $"the app to enter '{MainScene}' from '{EntryScene}'");
            yield return Bind();
            loadedScene = MainScene;
            if (ExpectsRos) yield return RemoveLeftoversOfEarlierRuns();
            OnSceneEntered();
        }

        /// <summary>Shapes the tests added through the Scene tab; whatever a failed test left is removed before the next one.</summary>
        protected static readonly List<GameObject> Spawned = new();

        private static void CleanUpAfterEarlierTest()
        {
            if (Context?.Selection != null) Context.Selection.ClearSelection();
            foreach (GameObject shape in Spawned)
                if (shape != null) Erupt.Obstacles.ObstacleFactory.Destroy(shape, Context.Environment);
            Spawned.Clear();
            if (MoveIt != null) MoveIt.StopPreview();
        }

        // An aborted run leaves its shapes in MoveIt (nothing told MoveIt the app went away), and
        // the next session mirrors them back as objects of the ROS side. Remove what this suite
        // creates, on the ROS side, so every run starts from the scene alone.
        private static readonly System.Text.RegularExpressions.Regex Leftover =
            new System.Text.RegularExpressions.Regex(@"^(live_.*|unity_(cube|sphere|cylinder|capsule)_\d+)$");

        private static IEnumerator RemoveLeftoversOfEarlierRuns()
        {
            if (MoveIt == null || MoveIt.PlanningScene == null) yield break;
            yield return LiveRos.Seconds(1.5f);   // the initial snapshot has been applied
            foreach (RobotInstance robot in Robots.ToArray())
            {
                bool detached = false;
                MoveIt.DetachAllFromRobot(robot, _ => detached = true);
                yield return LiveRos.Until(() => detached, 10f, _ => { });

                RosMessageTypes.Moveit.PlanningSceneMsg scene = null;
                yield return LiveRos.PlanningScene(robot, s => scene = s);
                foreach (var co in scene?.world?.collision_objects ?? Array.Empty<RosMessageTypes.Moveit.CollisionObjectMsg>())
                {
                    if (!Leftover.IsMatch(co.id) || MoveIt.PlanningScene.Mirror.IsUnityOwned(co.id)) continue;
                    Debug.Log($"[live] removing '{co.id}', left in the planning scene of '{robot.Id}' by an earlier run");
                    yield return ApplyOnRosSide(robot, new RosMessageTypes.Moveit.CollisionObjectMsg
                    {
                        id = co.id,
                        header = new RosMessageTypes.Std.HeaderMsg { frame_id = robot.PlanningFrameId },
                        operation = RosMessageTypes.Moveit.CollisionObjectMsg.REMOVE
                    }, mustSucceed: false);
                }
            }
            yield return LiveRos.Seconds(1f);
        }

        /// <summary>Find the app's services in the scene that is loaded now (again after a scene reload).</summary>
        protected static IEnumerator Bind()
        {
            Host = null;
            yield return LiveRos.Until(() => (Host = UnityEngine.Object.FindFirstObjectByType<PluginHost>()) != null && Host.Context != null
                                             && Host.Plugins.Count > 0, 30f, "the plugin host to register the scene's plugins");
            yield return null;

            Host.TryGet(out MoveItPlugin moveIt);
            Host.TryGet(out MtcPlugin mtc);
            Host.TryGet(out DemonstrationCollectionPlugin demonstrations);
            MoveIt = moveIt;
            Mtc = mtc;
            Demonstrations = demonstrations;
            Ui = Context.Ui as TierUiRig;
            if (Context.Router != null) Context.Router.Refused += Refusals.Add;
        }

        /// <summary>Called once, after the scene is up and the plugins are registered.</summary>
        protected virtual void OnSceneEntered() { }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Warning) Warnings.Add(condition);
            if (type == LogType.Exception) Exceptions.Add(condition + "\n" + stackTrace);
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) Errors.Add($"[{type}] {condition}");
        }

        // ---- the app's own entry points -----------------------------------------------------------

        /// <summary>The selectable the user points at to act on a robot's end effector.</summary>
        protected static ISelectable EndEffectorOf(RobotInstance robot)
        {
            Assert.IsNotNull(robot.Interaction, $"Robot '{robot.Id}' has no interaction controller.");
            Assert.IsNotNull(robot.Interaction.Handle, $"Robot '{robot.Id}' has no end-effector handle.");
            var marker = robot.Interaction.Handle.GetComponent<SelectableMarker>();
            Assert.IsNotNull(marker, $"The end-effector handle of '{robot.Id}' is not selectable.");
            Assert.AreEqual(SelectionKind.EndEffector, marker.Kind);
            return marker;
        }

        /// <summary>
        /// Select something and press a verb of its tier 2 menu, as the user does. Returns what
        /// the menu returned (a refusal when the verb cannot run).
        /// </summary>
        protected static InteractionRefusal Press(ISelectable target, string verbId)
        {
            Context.Selection.Select(target);
            Assert.IsNotNull(Ui, "The scene has no tier UI.");
            ContextualMenuModel menu = Ui.TierTwo.Model;
            Assert.AreSame(target, menu.Target, "The tier 2 menu follows the selection.");
            Verb verb = menu.Verbs.FirstOrDefault(v => v.Id == verbId);
            Assert.IsNotNull(verb, $"The menu of {target.Kind} has no verb '{verbId}' (it has: {string.Join(", ", menu.Verbs.Select(v => v.Id))}).");
            Assert.IsTrue(menu.CanInvoke(verb), $"The verb '{verbId}' is not pressable on {target.Kind}.");
            return Ui.TierTwo.Invoke(verb);
        }

        /// <summary>Drag a robot's end effector to a world position the way the handle does: IK steps over frames.</summary>
        protected static IEnumerator DragEndEffector(RobotInstance robot, Vector3 worldTarget, float tolerance = 0.01f, float timeoutSeconds = 10f)
        {
            IRobotModel model = robot.Model;
            model.BeginInteraction();
            float end = Time.realtimeSinceStartup + timeoutSeconds;
            try
            {
                while ((model.EndEffector.position - worldTarget).magnitude > tolerance && Time.realtimeSinceStartup < end)
                {
                    model.TrySolveToTarget(worldTarget);
                    yield return null;
                }
            }
            finally
            {
                model.EndInteraction();
            }
            yield return null;
        }

        /// <summary>A robot's joint state by ROS joint name, as its ROS side would report it.</summary>
        protected static Dictionary<string, float> UnityJoints(RobotInstance robot)
        {
            string[] names = robot.Names.ToRos(robot.Model.GetJointStateNames());
            float[] positions = robot.Model.GetJointStatePositions();
            var result = new Dictionary<string, float>();
            for (int i = 0; i < names.Length; i++) result[names[i]] = positions[i];
            return result;
        }

        protected static string Join(IEnumerable<string> lines) => string.Join("\n", lines);
    }
}
