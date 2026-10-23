using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros;
using Factory;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// Grabbing a selected billet starts the pick-and-place recording; letting go of it in the
    /// target zone stops it after a settle and sends the task. A re-grab during the settle, a
    /// release outside the vise, an unselected part and a manually started recording all leave
    /// the recording alone.
    /// </summary>
    public class GuidedRecordingTests
    {
        const float Settle = 0.2f;

        FakeRosBus bus;
        GameObject selectionGo, origin, recorderGo, station, guideGo, part, slotGo, viseGo;
        SelectionManager selection;
        PickPlaceTaskRecorder recorder;
        GrabbablePartSet billets;
        GuidedRecording recording;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            LogAssert.Expect(LogType.Error, "Ray Interactor is not assigned in SelectionManager.");
            selectionGo = new GameObject("SelectionManager");
            selection = selectionGo.AddComponent<SelectionManager>();
            origin = new GameObject("BaseTransform");

            recorderGo = new GameObject("MTCManager");
            recorder = recorderGo.AddComponent<PickPlaceTaskRecorder>();
            Set(recorder, "selectionManager", selection);
            Set(recorder, "worldOrigin", origin);

            slotGo = new GameObject("StockSlot0");
            slotGo.transform.position = new Vector3(0f, 1f, 0.4f);
            part = GrabbableCollisionObjectFactory.CreatePrimitive(PrimitiveType.Cube, "blank_1", new Vector3(0.08f, 0.06f, 0.05f), "blank_1", origin);
            part.transform.position = slotGo.transform.position;
            station = new GameObject("FR3 Station");
            billets = station.AddComponent<GrabbablePartSet>();
            billets.slots = new[] { slotGo.transform };
            billets.prePlaced = new[] { part };
            billets.worldOrigin = origin;

            guideGo = new GameObject("DemoGuide");
            recording = guideGo.AddComponent<GuidedRecording>();
            Set(recording, "recorder", recorder);
            Set(recording, "billets", billets);
            Set(recording, "selection", selection);
            Set(recording, "releaseSettleSeconds", Settle);
            yield return null;
            yield return null; // GrabbablePartSet.Start registered the part; GuidedRecording hooked it
            Assert.AreEqual(1, recording.HookedCount, "the billet's grab events are hooked");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in new[] { guideGo, station, part, slotGo, recorderGo, origin, selectionGo, viseGo })
                if (go != null) Object.DestroyImmediate(go);
            RosBus.Reset();
        }

        static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        MachineVise AddVise(Vector3 windowPosition)
        {
            viseGo = new GameObject("MachineFixture");
            var window = new GameObject("JawWindow").transform;
            window.SetParent(viseGo.transform, false);
            window.position = windowPosition;
            var vise = viseGo.AddComponent<MachineVise>();
            vise.jawWindow = window;
            vise.jaws = new SlidingPart[0];
            vise.openGap = 0.12f;
            vise.jawWidth = 0.16f;
            Set(recording, "targetVise", vise);
            return vise;
        }

        [UnityTest]
        public IEnumerator GrabThenRelease_RecordsAndSendsTheTask()
        {
            selection.SetSelectedObject(part);
            recording.NotifyGrabbed(part);
            Assert.IsTrue(recorder.IsRecording, "grabbing the selected billet starts the recording");
            Assert.IsTrue(recording.StartedByGuide);

            part.transform.position += new Vector3(0.3f, 0f, 0f);
            recording.NotifyReleased(part);
            Assert.IsTrue(recording.StopPending, "a release schedules the stop");
            yield return new WaitForSeconds(Settle + 0.3f);

            Assert.IsFalse(recorder.IsRecording, "stopped after the settle");
            Assert.IsFalse(recording.StartedByGuide);
            Assert.IsTrue(bus.Publishes.Any(p => p.Topic == "/pick_place_task"), "the task went out");
            Assert.Less(Vector3.Distance(part.transform.position, slotGo.transform.position), 1e-3f, "the billet is back at its pick pose");
        }

        [UnityTest]
        public IEnumerator ReGrabDuringTheSettle_CancelsTheStop()
        {
            selection.SetSelectedObject(part);
            recording.NotifyGrabbed(part);
            recording.NotifyReleased(part);
            yield return new WaitForSeconds(Settle * 0.25f);
            recording.NotifyGrabbed(part);
            Assert.IsFalse(recording.StopPending, "the re-grab cancels the pending stop");
            yield return new WaitForSeconds(Settle + 0.3f);
            Assert.IsTrue(recorder.IsRecording, "still recording while the user adjusts");
            Assert.IsFalse(bus.Publishes.Any(p => p.Topic == "/pick_place_task"));
        }

        [UnityTest]
        public IEnumerator ReleaseOutsideTheVise_KeepsRecording_InsideStops()
        {
            MachineVise vise = AddVise(new Vector3(1f, 1f, 1f));
            yield return null;
            selection.SetSelectedObject(part);
            recording.NotifyGrabbed(part);

            part.transform.position = new Vector3(2f, 1f, 1f);
            recording.NotifyReleased(part);
            yield return new WaitForSeconds(Settle + 0.3f);
            Assert.IsTrue(recorder.IsRecording, "a release beside the vise keeps the recording open");
            Assert.IsFalse(recording.StopPending);

            part.transform.position = vise.jawWindow.position + Vector3.up * 0.035f;
            recording.NotifyReleased(part);
            yield return new WaitForSeconds(Settle + 0.3f);
            Assert.IsFalse(recorder.IsRecording, "a release between the jaws stops it");
            Assert.IsTrue(bus.Publishes.Any(p => p.Topic == "/pick_place_task"));
        }

        [UnityTest]
        public IEnumerator ConveyorZone_StopsOnlyOnTheBelt()
        {
            var anchorGo = new GameObject("DropAnchor");
            anchorGo.transform.position = new Vector3(-1f, 0.75f, 0.7f);
            Set(recording, "conveyorAnchor", anchorGo.transform);
            Set(recording, "conveyorZoneRadius", 0.25f);
            recording.Zone = GuidedRecording.RecordingZone.Conveyor;
            Assert.IsTrue(GuidedRecording.OnConveyor(anchorGo.transform, new Vector3(-1.1f, 0.78f, 0.75f), 0.25f));
            Assert.IsFalse(GuidedRecording.OnConveyor(anchorGo.transform, new Vector3(-1.5f, 0.78f, 0.7f), 0.25f), "too far along the belt");
            Assert.IsFalse(GuidedRecording.OnConveyor(anchorGo.transform, new Vector3(-1f, 1.2f, 0.7f), 0.25f), "held high above the belt");

            selection.SetSelectedObject(part);
            recording.NotifyGrabbed(part);
            part.transform.position = new Vector3(0.5f, 1f, 0.5f);
            recording.NotifyReleased(part);
            yield return new WaitForSeconds(Settle + 0.3f);
            Assert.IsTrue(recorder.IsRecording, "a release away from the belt keeps recording");

            part.transform.position = anchorGo.transform.position + Vector3.up * 0.03f;
            recording.NotifyReleased(part);
            yield return new WaitForSeconds(Settle + 0.3f);
            Assert.IsFalse(recorder.IsRecording, "a release on the belt stops it");
            Assert.IsTrue(bus.Publishes.Any(p => p.Topic == "/pick_place_task"));
            Object.DestroyImmediate(anchorGo);
        }

        [UnityTest]
        public IEnumerator UnselectedBillet_DoesNotRecord()
        {
            selection.ClearSelection();
            recording.NotifyGrabbed(part);
            Assert.IsFalse(recorder.IsRecording, "the recorder needs the selection; an unselected grab is ignored");
            yield return null;
        }

        [UnityTest]
        public IEnumerator ManualRecording_IsLeftAlone()
        {
            selection.SetSelectedObject(part);
            recorder.StartRecording();
            Assert.IsTrue(recorder.IsRecording);
            Assert.IsFalse(recording.StartedByGuide);
            recording.NotifyReleased(part);
            Assert.IsFalse(recording.StopPending, "a recording the dashboard started is not stopped by the guide");
            yield return new WaitForSeconds(Settle + 0.3f);
            Assert.IsTrue(recorder.IsRecording);
            recorder.CancelRecording();
            Assert.IsFalse(recorder.IsRecording);
            Assert.IsFalse(bus.Publishes.Any(p => p.Topic == "/pick_place_task"), "cancel sends nothing");
        }

        [UnityTest]
        public IEnumerator AutoRecordOff_IgnoresGrabs()
        {
            recording.AutoRecord = false;
            selection.SetSelectedObject(part);
            recording.NotifyGrabbed(part);
            Assert.IsFalse(recorder.IsRecording);
            yield return null;
        }
    }
}
