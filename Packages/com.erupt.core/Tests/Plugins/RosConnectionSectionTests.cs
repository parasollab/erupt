using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;
using Erupt.Ui;

namespace Erupt.Plugins.Tests
{
    /// <summary>
    /// The ROS address as the user sets it while the app runs: the Settings tab says where the
    /// app connects and whether it is connected, a keypad takes another address, and the
    /// address is used at once and remembered. The plugin host puts the section there by
    /// itself and applies a remembered address before any plugin registers.
    /// </summary>
    public class RosConnectionSectionTests
    {
        private const string BuiltIn = "192.168.1.48";

        private MemoryUserSettingsStore store;
        private FakeRosBus bus;
        private RobotRegistry robots;
        private GameObject routerGo, uiGo, hostGo;
        private InteractionRouter router;
        private TierUiRig rig;
        private PluginHost host;
        private readonly List<InteractionRefusal> refusals = new List<InteractionRefusal>();

        [SetUp]
        public void SetUp()
        {
            store = new MemoryUserSettingsStore();
            UserSettings.Store = store;
            bus = new FakeRosBus { Host = BuiltIn, DefaultHost = BuiltIn };
            robots = FakeRobots.Three(bus);

            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            refusals.Clear();
            router.Refused += refusals.Add;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(hostGo);
            Object.DestroyImmediate(uiGo);
            Object.DestroyImmediate(routerGo);
            FakeRobots.DestroyAll(robots);
            UserSettings.Store = null;
        }

        private void MakeUi(bool awake = true)
        {
            uiGo = new GameObject("ERUPT");
            uiGo.SetActive(awake);
            uiGo.AddComponent<SelectionService>();
            rig = uiGo.AddComponent<TierUiRig>();
        }

        private PluginHost MakeHost(IRosBus ros, bool start = true)
        {
            hostGo = new GameObject("host");
            hostGo.SetActive(false);
            host = hostGo.AddComponent<PluginHost>();
            host.Initialise(new EruptContext { Ros = ros, Robots = robots, Ui = rig, Router = router, Undo = new UndoStack() });
            host.DiscoverInScene = false;
            if (start) hostGo.SetActive(true);
            return host;
        }

        // The Settings tab, on show: the keypad is put away when the user cannot see it.
        private IEnumerator Started()
        {
            yield return null;
            yield return null;
            rig.AddTab("scene", "Scene", null).Order = 0;
            rig.SummonTab(SettingsTab.TabId);
            yield return null;
        }

        private T Control<T>(string name) where T : Component =>
            rig.Settings.Tab.Content.GetComponentsInChildren<T>(true).First(c => c.name == name);

        private bool Has(string name) =>
            rig.Settings.Tab.Content.GetComponentsInChildren<Transform>(true).Any(c => c.name == name);

        private static string TextOf(Component control) => control.GetComponentInChildren<TextMeshProUGUI>().text;

        private void Press(params string[] keys)
        {
            foreach (string key in keys) Control<Button>(RosConnectionSection.KeyName(key)).onClick.Invoke();
        }

        private string Status => Control<TextMeshProUGUI>("RosStatus").text;

        [UnityTest]
        public IEnumerator TheHost_PutsTheSectionOnTheSettingsTab_AndItSaysWhereTheAppConnects()
        {
            MakeUi();
            MakeHost(bus);
            yield return Started();

            Assert.IsNotNull(host.RosConnection);
            CollectionAssert.AreEqual(new[] { RosConnectionSection.SectionId }, rig.Settings.Sections.ToArray());
            Assert.AreEqual($"Connected to ROS at {BuiltIn}:10000", Status);
            Assert.AreEqual(RosConnectionSection.ChangeLabel, TextOf(Control<Button>("RosAddress")));
            Assert.AreEqual(1, rig.Settings.InteractiveElementCount, "One button until the user asks for the keypad (Part 8: at most 7).");
            Assert.IsFalse(Has("RosAddressEntry"), "No keypad until then.");
            Assert.IsEmpty(bus.ConnectCalls, "Nothing was saved, so the bus is left where the build sends it.");

            // The line follows the connection.
            bus.HasConnectionError = true;
            yield return null;
            Assert.AreEqual($"Not connected to ROS at {BuiltIn}:10000", Status);
            bus.HasConnectionError = false;
            bus.HasConnectionThread = false;
            yield return null;
            Assert.AreEqual($"Not connected to ROS at {BuiltIn}:10000", Status, "No connection thread is not a connection either.");
            bus.HasConnectionThread = true;
            yield return null;
            Assert.AreEqual($"Connected to ROS at {BuiltIn}:10000", Status);

            // Without the host the section is gone, and the tab with it.
            Object.DestroyImmediate(hostGo);
            yield return null;
            Assert.IsNull(rig.Settings.Tab);
        }

        [UnityTest]
        public IEnumerator TheKeypad_TakesAnAddress_ConnectsThere_AndRemembersIt()
        {
            MakeUi();
            MakeHost(bus);
            yield return Started();
            RosConnectionSection section = host.RosConnection;

            Control<Button>("RosAddress").onClick.Invoke();
            Assert.IsTrue(section.IsOpen);
            Assert.AreEqual(RosConnectionSection.CancelLabel, TextOf(Control<Button>("RosAddress")), "The button that opened the keypad puts it away.");
            Assert.AreEqual(BuiltIn, section.Entry, "The entry starts from the address in use.");
            Assert.AreEqual(BuiltIn + "_", Control<TextMeshProUGUI>("RosAddressEntry").text);
            Assert.AreEqual($"Connected to ROS at {BuiltIn}:10000", Status, "The connection is still shown.");
            StringAssert.Contains("10000", Control<TextMeshProUGUI>("RosAddressHint").text);
            Assert.AreEqual(14, rig.Settings.InteractiveElementCount, "Cancel, twelve keys and Connect.");

            Press(RosConnectionSection.DeleteLabel, RosConnectionSection.DeleteLabel);
            Assert.AreEqual("192.168.1.", section.Entry);
            Press("0", "5");
            Assert.AreEqual("192.168.1.05", section.Entry);
            Assert.AreEqual("192.168.1.05_", Control<TextMeshProUGUI>("RosAddressEntry").text);
            Assert.IsEmpty(bus.ConnectCalls, "Nothing happens until Connect.");
            yield return null;
            Assert.IsTrue(section.IsOpen, "The keypad stays while the user is on the tab.");

            Control<Button>("RosConnect").onClick.Invoke();
            CollectionAssert.AreEqual(new[] { ("192.168.1.5", 10000) }, bus.ConnectCalls, "At once, and without the leading zero.");
            Assert.IsTrue(store.TryGetString(RosEndpointSetting.AddressKey, out string saved));
            Assert.AreEqual("192.168.1.5", saved);
            Assert.IsTrue(store.TryGetString(RosEndpointSetting.BuiltInKey, out string against));
            Assert.AreEqual($"{BuiltIn}:10000", against);
            Assert.AreEqual(1, store.Saves, "Written when it is set, not when the app closes.");
            Assert.IsEmpty(refusals);

            Assert.IsFalse(section.IsOpen, "Connect puts the keypad away.");
            Assert.IsFalse(Has("RosAddressEntry"));
            Assert.AreEqual(RosConnectionSection.ChangeLabel, TextOf(Control<Button>("RosAddress")));
            yield return null;
            Assert.AreEqual("Connected to ROS at 192.168.1.5:10000", Status);
        }

        [UnityTest]
        public IEnumerator AnEntryThatIsNoAddress_IsRefusedWithAReason_AndChangesNothing()
        {
            MakeUi();
            MakeHost(bus);
            yield return Started();
            RosConnectionSection section = host.RosConnection;

            Control<Button>("RosAddress").onClick.Invoke();
            Press(RosConnectionSection.DeleteLabel, RosConnectionSection.DeleteLabel, "9", "9", "9");
            Assert.AreEqual("192.168.1.999", section.Entry);
            Control<Button>("RosConnect").onClick.Invoke();

            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RosConnectionSection.InvalidReason, refusals[0].Reason);
            Assert.AreEqual(RefusalCode.Unspecified, refusals[0].Code);
            Assert.Less(Vector3.Distance(Control<TextMeshProUGUI>("RosAddressEntry").transform.position, refusals[0].WorldPoint), 1e-4f,
                "The reason is shown at the entry.");
            Assert.IsEmpty(bus.ConnectCalls);
            Assert.IsFalse(store.Has(RosEndpointSetting.AddressKey));
            Assert.IsTrue(section.IsOpen, "The entry stays, to be corrected.");
            Assert.AreEqual("192.168.1.999", section.Entry);

            // The keys stop at the length of an address, and Del at nothing.
            Press("1", "1", "1", "1", "1");
            Assert.AreEqual(RosAddress.MaxLength, section.Entry.Length);
            Assert.AreEqual("192.168.1.99911", section.Entry);
            for (int i = 0; i < 20; i++) Press(RosConnectionSection.DeleteLabel);
            Assert.AreEqual("", section.Entry);
            Assert.AreEqual("_", Control<TextMeshProUGUI>("RosAddressEntry").text);
            Press(".", "7");
            Assert.AreEqual(".7", section.Entry);
        }

        [UnityTest]
        public IEnumerator TheKeypad_IsPutAway_ByItsButton_AndWhenTheUserLeavesIt()
        {
            MakeUi();
            MakeHost(bus);
            yield return Started();
            RosConnectionSection section = host.RosConnection;

            Control<Button>("RosAddress").onClick.Invoke();
            Press(RosConnectionSection.DeleteLabel);
            Control<Button>("RosAddress").onClick.Invoke();
            Assert.IsFalse(section.IsOpen);
            Assert.IsEmpty(bus.ConnectCalls, "Cancel connects nowhere.");
            Assert.IsFalse(store.Has(RosEndpointSetting.AddressKey));
            Assert.AreEqual(1, rig.Settings.InteractiveElementCount);

            // Another tab.
            Control<Button>("RosAddress").onClick.Invoke();
            Assert.AreEqual(BuiltIn, section.Entry, "What was half entered is not kept.");
            rig.TierThree.SelectTab("scene");
            yield return null;
            Assert.IsFalse(section.IsOpen, "The keypad does not wait behind another tab.");
            Assert.IsNull(rig.Settings.Showing);

            // The panel closed.
            rig.SummonTab(SettingsTab.TabId);
            yield return null;
            Control<Button>("RosAddress").onClick.Invoke();
            Assert.IsTrue(section.IsOpen);
            rig.ToggleTierThree();
            Assert.IsFalse(rig.TierThree.IsOpen);
            yield return null;
            Assert.IsFalse(section.IsOpen, "Nor behind a closed panel.");
            rig.ToggleTierThree();
            yield return null;
            Assert.AreEqual(RosConnectionSection.ChangeLabel, TextOf(Control<Button>("RosAddress")));
        }

        [UnityTest]
        public IEnumerator WithOtherSettings_TheKeypadHasTheTabToItself_AndGivesItBack()
        {
            MakeUi();
            MakeHost(bus);
            yield return Started();
            rig.Settings.AddSection("other", column =>
                UiBuilder.CreateSlider("Other", column, "Other", 0f, 1f, 1f, new Vector2(300f, 76f), _ => { }));
            yield return null;
            Assert.AreEqual(2, rig.Settings.InteractiveElementCount);
            Assert.AreEqual($"Connected to ROS at {BuiltIn}:10000", Status, "Built again with the tab, the line is still there.");

            Control<Button>("RosAddress").onClick.Invoke();
            Assert.AreEqual(RosConnectionSection.SectionId, rig.Settings.Showing);
            Assert.IsFalse(Has("Other"), "The other setting makes room.");
            Assert.AreEqual(14, rig.Settings.InteractiveElementCount);

            Control<Button>("RosAddress").onClick.Invoke();
            Assert.IsNull(rig.Settings.Showing);
            Assert.IsTrue(Has("Other"));
            Assert.AreEqual(2, rig.Settings.InteractiveElementCount);
        }

        [UnityTest]
        public IEnumerator EnteringTheBuiltInAddress_ForgetsWhatWasSet()
        {
            store.SetString(RosEndpointSetting.AddressKey, "192.168.1.5");
            store.SetString(RosEndpointSetting.BuiltInKey, $"{BuiltIn}:10000");
            MakeUi();
            MakeHost(bus);
            yield return Started();
            CollectionAssert.AreEqual(new[] { ("192.168.1.5", 10000) }, bus.ConnectCalls, "The remembered address was applied at the start.");
            Assert.AreEqual("Connected to ROS at 192.168.1.5:10000", Status);

            Control<Button>("RosAddress").onClick.Invoke();
            Assert.AreEqual("192.168.1.5", host.RosConnection.Entry);
            Press(RosConnectionSection.DeleteLabel, "4", "8");
            Control<Button>("RosConnect").onClick.Invoke();

            Assert.AreEqual((BuiltIn, 10000), bus.ConnectCalls.Last());
            Assert.IsFalse(store.Has(RosEndpointSetting.AddressKey), "The built-in address is the default again.");
            Assert.IsFalse(store.Has(RosEndpointSetting.BuiltInKey));
        }

        // The host may start before the tier UI has woken.
        [UnityTest]
        public IEnumerator ATierUiThatWakesLater_StillGetsTheSection()
        {
            MakeUi(awake: false);
            MakeHost(bus);
            yield return null;
            yield return null;
            Assert.IsNull(rig.Settings, "The rig has not built its tiers yet.");
            Assert.IsNotNull(host.RosConnection);

            uiGo.SetActive(true);
            yield return null;
            yield return null;
            Assert.IsNotNull(rig.Settings.Tab);
            CollectionAssert.Contains(rig.Settings.Sections.ToArray(), RosConnectionSection.SectionId);
        }

        [UnityTest]
        public IEnumerator NoSection_WithoutAnEndpoint_OrWhenTheHostIsToldNotTo()
        {
            MakeUi();
            MakeHost(null);
            yield return null;
            yield return null;
            Assert.IsNull(host.RosConnection, "No bus, nothing to set.");
            Assert.IsNull(rig.Settings.Tab, "And no dead control.");
            Object.DestroyImmediate(hostGo);

            MakeHost(bus, start: false);
            host.RosConnectionInSettings = false;
            hostGo.SetActive(true);
            yield return null;
            yield return null;
            Assert.IsNull(host.RosConnection);
            Assert.IsNull(rig.Settings.Tab);
        }

        // ---------- the remembered address ----------

        private sealed class Probe : IEruptPlugin
        {
            private readonly FakeRosBus bus;
            public Probe(FakeRosBus bus) { this.bus = bus; }
            public string Id => "probe";
            public string DisplayName => "probe";
            public IReadOnlyList<string> DependsOn => System.Array.Empty<string>();
            public string HostAtRegistration;
            public void OnRegister(IEruptContext context) => HostAtRegistration = bus.Host;
            public void OnUnregister(IEruptContext context) { }
        }

        [UnityTest]
        public IEnumerator ARememberedAddress_IsOnTheBus_BeforeAnyPluginRegisters_AndAppliedOnce()
        {
            store.SetString(RosEndpointSetting.AddressKey, "10.0.0.7");
            store.SetString(RosEndpointSetting.BuiltInKey, $"{BuiltIn}:10000");
            MakeUi();
            MakeHost(bus, start: false);

            var probe = new Probe(bus);
            host.Register(probe);
            Assert.AreEqual("10.0.0.7", probe.HostAtRegistration, "The plugin's first subscription would open the connection there.");
            CollectionAssert.AreEqual(new[] { ("10.0.0.7", 10000) }, bus.ConnectCalls);

            hostGo.SetActive(true);
            yield return null;
            yield return null;
            Assert.AreEqual(1, bus.ConnectCalls.Count, "Start does not apply it a second time.");
            Assert.AreEqual(0, store.Saves, "Reading the setting writes nothing.");
        }

        [Test]
        public void ARememberedAddress_AppliesOnlyToTheBuiltInAddressItReplaced()
        {
            // Nothing saved.
            Assert.IsFalse(RosEndpointSetting.ApplySaved(bus));
            Assert.IsFalse(RosEndpointSetting.ApplySaved(null));
            Assert.IsEmpty(bus.ConnectCalls);

            RosEndpointSetting.Save(bus, "10.0.0.7");
            Assert.IsTrue(RosEndpointSetting.TryGetSaved(bus, out string saved));
            Assert.AreEqual("10.0.0.7", saved);

            // The ROS settings were changed, or another build was installed: its address is used.
            var rebuilt = new FakeRosBus { Host = "192.168.1.60", DefaultHost = "192.168.1.60" };
            Assert.IsFalse(RosEndpointSetting.ApplySaved(rebuilt));
            Assert.IsEmpty(rebuilt.ConnectCalls);
            Assert.IsTrue(store.Has(RosEndpointSetting.AddressKey), "Applying changes nothing that is saved.");

            // Another port is another built-in endpoint.
            var otherPort = new FakeRosBus { Host = BuiltIn, DefaultHost = BuiltIn, Port = 10001, DefaultPort = 10001 };
            Assert.IsFalse(RosEndpointSetting.ApplySaved(otherPort));

            // The build it was set on.
            Assert.IsTrue(RosEndpointSetting.ApplySaved(bus));
            CollectionAssert.AreEqual(new[] { ("10.0.0.7", 10000) }, bus.ConnectCalls);

            // Something that is not an address is not used.
            store.SetString(RosEndpointSetting.AddressKey, "not.an.address");
            bus.ConnectCalls.Clear();
            Assert.IsFalse(RosEndpointSetting.ApplySaved(bus));
            Assert.IsEmpty(bus.ConnectCalls);
        }
    }
}
