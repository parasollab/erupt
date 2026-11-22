using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Ros;
using Erupt.Ros.Tests;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using Erupt.Environment;
using Erupt.Plugins.MoveIt;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>
    /// The pick/place recorder: the pick pose is where the object stood when it was first
    /// watched, whatever happens to the selection afterwards; a recording can be cancelled;
    /// and a release can be reported by something other than an XRI grab.
    /// </summary>
    public class PickPlaceTaskRecorderTests
    {
        const string k_TaskTopic = "/pick_place_task";

        static readonly Vector3 Stand = new Vector3(0.4f, 0.1f, 0.2f);
        static readonly Vector3 MidAir = new Vector3(0.5f, 0.6f, 0.2f);
        static readonly Vector3 Target = new Vector3(0.8f, 0.1f, -0.3f);

        FakeRosBus bus;
        GameObject selectionGo, recorderGo, part, other;
        SelectionService selection;
        PickPlaceTaskRecorder recorder;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);

            selectionGo = new GameObject("selection");
            selection = selectionGo.AddComponent<SelectionService>();
            var highlighter = selectionGo.AddComponent<SelectionHighlighter>();   // finds the service in Awake

            // No PickPlaceClient next to it: the captured task goes out on the task topic.
            recorderGo = new GameObject("recorder");
            recorder = recorderGo.AddComponent<PickPlaceTaskRecorder>();
            recorder.Initialise(bus.Root);   // the task topic is relative to the robot's namespace
            typeof(PickPlaceTaskRecorder).GetField("selectionManager", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(recorder, highlighter);

            part = MakePart("part", Stand);
            other = MakePart("other", new Vector3(-1f, 0.1f, 0f));
            yield return null; // Start() of every component
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in new[] { recorderGo, part, other, selectionGo })
                if (go != null) Object.DestroyImmediate(go);
            RosBus.Reset();
        }

        [UnityTest]
        public IEnumerator ReleaseReportedFromOutside_SendsThePlacePose_AndPutsTheObjectBack()
        {
            string capturedId = null, completedId = null;
            Vector3 capturedPosition = Vector3.zero;
            recorder.OnTaskCaptured += (id, position, rotation) => { capturedId = id; capturedPosition = position; };
            recorder.OnRecordingComplete += id => completedId = id;

            Select(part);
            recorder.StartRecording();
            Assert.IsTrue(part.GetComponent<CollisionObjectPublisher>().pausePublishing, "The demonstration must not reach MoveIt.");

            part.transform.position = Target;
            recorder.NotifyObjectReleased(other);   // some other object: not a release of the watched one
            recorder.NotifyObjectReleased(part);
            recorder.StopRecording();

            Assert.AreEqual("part", capturedId);
            AssertClose(Target, capturedPosition);
            Assert.AreEqual("part", completedId);
            var task = (PickPlaceTaskMsg)bus.PublishedOn(k_TaskTopic).Single();
            Assert.AreEqual("part", task.object_id);
            // Unity (x, y, z) is ROS (x, z, y).
            Assert.AreEqual(Target.x, task.place_pose.pose.position.x, 1e-5);
            Assert.AreEqual(Target.z, task.place_pose.pose.position.y, 1e-5);
            Assert.AreEqual(Target.y, task.place_pose.pose.position.z, 1e-5);
            AssertClose(Stand, part.transform.position);
            Assert.IsFalse(part.GetComponent<CollisionObjectPublisher>().pausePublishing);
            yield return null;
        }

        // The static provider is overridden with the same fake in SetUp, so a recorder that
        // read it would register its publisher without being given a bus.
        [UnityTest]
        public IEnumerator WithoutABus_TheTaskPublisherIsNotRegistered_UntilTheRecorderIsGivenOne()
        {
            var lateGo = new GameObject("recorder without a bus");
            try
            {
                var late = lateGo.AddComponent<PickPlaceTaskRecorder>();
                bus.Clear();
                yield return null; // Start, with no bus.

                CollectionAssert.DoesNotContain(bus.RegisteredPublishers, k_TaskTopic);

                late.Initialise(bus.Root);

                CollectionAssert.Contains(bus.RegisteredPublishers, k_TaskTopic);
            }
            finally
            {
                Object.DestroyImmediate(lateGo);
            }
        }

        [UnityTest]
        public IEnumerator ReleaseOfAnotherObject_IsNotARelease()
        {
            string discarded = null;
            recorder.OnRecordingDiscarded += reason => discarded = reason;

            Select(part);
            recorder.StartRecording();
            part.transform.position = Target;
            recorder.NotifyObjectReleased(other);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("nothing sent"));
            recorder.StopRecording();

            Assert.IsEmpty(bus.PublishedOn(k_TaskTopic));
            Assert.That(discarded, Does.Contain("never released"));
            yield return null;
        }

        // The user carries the object, the selection is cleared, and they select the object again
        // to grip it: the pick pose stays the one the object started from.
        [UnityTest]
        public IEnumerator ReselectingTheWatchedObject_KeepsItsPickPose()
        {
            Select(part);
            recorder.StartRecording();
            var publisher = part.GetComponent<CollisionObjectPublisher>();

            part.transform.position = MidAir;
            selection.ClearSelection();
            Select(part);
            Assert.IsTrue(publisher.pausePublishing, "Still watched: the hovering pose must not be published.");

            part.transform.position = Target;
            recorder.NotifyObjectReleased(part);
            recorder.StopRecording();

            Assert.AreEqual(1, bus.PublishedOn(k_TaskTopic).Count());
            AssertClose(Stand, part.transform.position);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SelectingAnotherObjectAndComingBack_KeepsTheFirstPickPose()
        {
            Select(part);
            recorder.StartRecording();
            part.transform.position = MidAir;

            Select(other);
            Select(part);
            part.transform.position = Target;
            recorder.NotifyObjectReleased(part);
            recorder.StopRecording();

            var task = (PickPlaceTaskMsg)bus.PublishedOn(k_TaskTopic).Single();
            Assert.AreEqual("part", task.object_id);
            AssertClose(Stand, part.transform.position);

            // The next recording starts afresh: the pick pose is where the object stands then.
            part.transform.position = MidAir;
            recorder.StartRecording();
            part.transform.position = Target;
            recorder.NotifyObjectReleased(part);
            recorder.StopRecording();
            AssertClose(MidAir, part.transform.position);
            yield return null;
        }

        [UnityTest]
        public IEnumerator CancelRecording_SendsNothing_PutsTheObjectBack_AndReportsWhy()
        {
            string discarded = null;
            int completed = 0;
            recorder.OnRecordingDiscarded += reason => discarded = reason;
            recorder.OnRecordingComplete += _ => completed++;

            recorder.CancelRecording();
            Assert.IsNull(discarded, "Nothing to cancel when not recording.");

            Select(part);
            recorder.StartRecording();
            part.transform.position = Target;
            recorder.NotifyObjectReleased(part);
            recorder.CancelRecording();

            Assert.IsFalse(recorder.IsRecording);
            Assert.IsEmpty(bus.PublishedOn(k_TaskTopic));
            Assert.AreEqual(0, completed);
            Assert.AreEqual("recording cancelled", discarded);
            AssertClose(Stand, part.transform.position);
            Assert.IsFalse(part.GetComponent<CollisionObjectPublisher>().pausePublishing);

            // Selection changes after the cancel are no longer followed.
            Select(other);
            Assert.IsFalse(other.GetComponent<CollisionObjectPublisher>().pausePublishing);
            yield return null;
        }

        // ---------- helpers ----------

        void Select(GameObject go) => selection.Select(go.GetComponent<SelectableMarker>());

        static GameObject MakePart(string id, Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = id;
            go.transform.position = position;
            go.transform.localScale = Vector3.one * 0.05f;
            GrabbableCollisionObjectFactory.Configure(go, id, null);
            SelectableMarker.Ensure(go, SelectionKind.Obstacle);
            return go;
        }

        static void AssertClose(Vector3 expected, Vector3 actual) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-4f, $"expected {expected} but was {actual}");
    }
}
