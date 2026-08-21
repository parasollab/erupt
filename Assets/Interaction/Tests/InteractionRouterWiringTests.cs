using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;

namespace Erupt.Interaction.Tests
{
    /// <summary>
    /// Play-mode wiring tests. These exist because a scene can pass every static check —
    /// components present, fields assigned — and still route nothing, if the router cannot
    /// reach its sources once Awake runs.
    /// </summary>
    public class InteractionRouterWiringTests
    {
        private GameObject rig;

        [TearDown]
        public void TearDown()
        {
            InteractionSampleBus.Reset();
            if (rig != null) Object.DestroyImmediate(rig);
        }

        // Regression: the migrated rig puts the router under XR Origin and the backends
        // under Camera Offset/<hand>/Robot Ray. Those are not the router's children, so a
        // GetComponentsInChildren search registered nothing and no input reached any
        // feature — selection, robot control and grabbing all silently stopped working.
        [UnityTest]
        public IEnumerator Router_RegistersSourcesOutsideItsOwnHierarchy()
        {
            rig = new GameObject("XR Origin");

            var sourceGo = new GameObject("Robot Ray");
            sourceGo.transform.SetParent(rig.transform);
            var source = sourceGo.AddComponent<TestInteractionSource>();

            var routerGo = new GameObject("Interaction Router");
            routerGo.transform.SetParent(rig.transform);
            var router = routerGo.AddComponent<InteractionRouter>();

            yield return null;

            Assert.Contains(source, router.Sources.ToList(),
                "Router must find sources anywhere in the scene, not only among its own children.");
        }

        [UnityTest]
        public IEnumerator RegisteredSource_ActuallyDeliversIntents()
        {
            rig = new GameObject("XR Origin");

            var sourceGo = new GameObject("Robot Ray");
            sourceGo.transform.SetParent(rig.transform);
            var source = sourceGo.AddComponent<TestInteractionSource>();

            var routerGo = new GameObject("Interaction Router");
            routerGo.transform.SetParent(rig.transform);
            var router = routerGo.AddComponent<InteractionRouter>();

            yield return null;

            int selects = 0;
            router.Select += _ => selects++;
            source.EmitSelect(Modality.Controller, 1f);

            Assert.AreEqual(1, selects, "A registered source must deliver intents to the router.");
        }
    }
}
