using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Erupt.Interaction;

namespace Erupt.Ui.Tests
{
    /// <summary>
    /// The refusal toast: a refusal raised on the router shows as text at the place it is
    /// about, replaces an older one, fades out, and never takes a press.
    /// </summary>
    public class RefusalToastTests
    {
        private GameObject routerGo, toastGo, cameraGo, subjectGo;
        private InteractionRouter router;
        private RefusalToast toast;

        [SetUp]
        public void SetUp()
        {
            cameraGo = new GameObject("head") { tag = "MainCamera" };
            cameraGo.AddComponent<Camera>();
            cameraGo.transform.position = new Vector3(0f, 1.5f, -2f);

            // A router without input sources says so; these tests only use its refusal channel.
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();

            toastGo = new GameObject("toast");
            toast = toastGo.AddComponent<RefusalToast>();
            toast.SetRouter(router);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(toastGo);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(cameraGo);
            if (subjectGo != null) Object.DestroyImmediate(subjectGo);
        }

        [Test]
        public void NothingIsShown_UntilSomethingIsRefused()
        {
            Assert.IsFalse(toast.IsShowing);
        }

        [Test]
        public void ARefusalOnTheRouter_ShowsItsReason_NearItsWorldPoint()
        {
            var point = new Vector3(0.5f, 1f, 1f);

            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.OutOfReach, "Target is out of reach by 12 cm.", point));

            Assert.IsTrue(toast.IsShowing);
            Assert.AreEqual("Target is out of reach by 12 cm.", toast.Text);
            Assert.AreEqual(RefusalCode.OutOfReach, toast.Last.Code);
            Assert.Less(Vector3.Distance(toast.Position, point), 0.25f, "The text is at the point of failure.");
            Assert.Less(Vector3.Distance(toast.Position, cameraGo.transform.position),
                        Vector3.Distance(point, cameraGo.transform.position),
                        "It is pulled toward the viewer, so geometry at the point does not hide it.");
        }

        [Test]
        public void ARefusalWithOnlyASubject_ShowsAtTheSubject()
        {
            subjectGo = new GameObject("link");
            subjectGo.transform.position = new Vector3(-1f, 0.5f, 2f);

            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "j1 is at its upper limit.", Vector3.zero, subjectGo.transform));

            Assert.IsTrue(toast.IsShowing);
            Assert.Less(Vector3.Distance(toast.Position, subjectGo.transform.position), 0.25f);
        }

        [Test]
        public void ARefusalWithoutAPlace_ShowsInFrontOfTheViewer()
        {
            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.PlannerUnavailable, "MoveIt is not connected.", Vector3.zero));

            Assert.IsTrue(toast.IsShowing);
            Vector3 fromHead = toast.Position - cameraGo.transform.position;
            Assert.Greater(Vector3.Dot(fromHead.normalized, cameraGo.transform.forward), 0.99f, "Where the user is looking.");
            Assert.That(fromHead.magnitude, Is.InRange(0.3f, 1.5f));
        }

        [Test]
        public void ANewerRefusal_ReplacesTheOneOnShow()
        {
            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.OutOfReach, "first", new Vector3(1f, 1f, 1f)));
            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "second", new Vector3(-1f, 1f, 1f)));

            Assert.AreEqual("second", toast.Text);
            Assert.Less(Vector3.Distance(toast.Position, new Vector3(-1f, 1f, 1f)), 0.25f);
        }

        [Test]
        public void TheToast_TakesNoPresses()
        {
            router.ReportRefusal(InteractionRefusal.Refuse("anything", new Vector3(0f, 1f, 0f)));

            Assert.IsEmpty(toastGo.GetComponentsInChildren<Collider>(true), "No collider: rays pass through to the world.");
            Assert.IsEmpty(toastGo.GetComponentsInChildren<GraphicRaycaster>(true));
            Assert.IsEmpty(toastGo.GetComponentsInChildren<Selectable>(true), "A readout, not a control.");
            foreach (Graphic graphic in toastGo.GetComponentsInChildren<Graphic>(true))
                Assert.IsFalse(graphic.raycastTarget, graphic.name);
        }

        [UnityTest]
        public IEnumerator TheToast_FadesOutAndHides()
        {
            Set("visibleSeconds", 0.05f);
            Set("fadeSeconds", 0.1f);

            router.ReportRefusal(InteractionRefusal.Refuse("gone soon", new Vector3(0f, 1f, 0f)));
            Assert.IsTrue(toast.IsShowing);

            float deadline = Time.realtimeSinceStartup + 3f;
            while (toast.IsShowing && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsFalse(toast.IsShowing);
            Assert.AreEqual("gone soon", toast.Text, "The last text stays readable for tests and logs.");
        }

        [Test]
        public void ADisabledToast_StopsListening()
        {
            toast.enabled = false;

            router.ReportRefusal(InteractionRefusal.Refuse("unheard", Vector3.one));

            Assert.IsFalse(toast.IsShowing);
        }

        [Test]
        public void TheTierUiRig_BringsAToast_ThatListensToTheScenesRouter()
        {
            var rigGo = new GameObject("rig");
            try
            {
                var rig = rigGo.AddComponent<TierUiRig>();

                Assert.IsNotNull(rig.Refusals, "The reference scene shows refusals without extra setup.");
                router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "from the rig's router", Vector3.one));

                Assert.IsTrue(rig.Refusals.IsShowing);
                Assert.AreEqual("from the rig's router", rig.Refusals.Text);
            }
            finally
            {
                Object.DestroyImmediate(rigGo);
            }
        }

        private void Set(string field, float value) =>
            typeof(RefusalToast).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(toast, value);
    }
}
