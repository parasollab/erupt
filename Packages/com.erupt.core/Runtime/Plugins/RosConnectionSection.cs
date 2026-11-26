using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Ros;
using Erupt.Ui;

namespace Erupt.Plugins
{
    /// <summary>
    /// The "ROS connection" section of the tier 3 Settings tab: where the app connects, whether
    /// it is connected, and a keypad to enter another address. The address takes effect at
    /// once (<see cref="IRosEndpoint.ConnectTo"/>) and is kept between sessions
    /// (<see cref="RosEndpointSetting"/>).
    /// </summary>
    /// <remarks>
    /// The plugin host adds this to itself when the bus has an endpoint it can change and the
    /// scene has a tier UI; nothing is wired per scene.
    ///
    /// The keypad is one entry control made of twelve keys. It is shown only after
    /// <see cref="ChangeLabel"/> was pressed, has the Settings tab to itself while it is
    /// (<see cref="SettingsTab.ShowOnly"/>), and is put away by the same button, by
    /// <see cref="ConnectLabel"/>, and when the user leaves the tab or closes the panel. Every
    /// key is a uGUI button, so the ray with the trigger, a poke and the mouse all press it.
    ///
    /// The section reads the state of the connection each frame and subscribes to nothing, so
    /// it leaves nothing behind on a bus that outlives the scene.
    /// </remarks>
    public sealed class RosConnectionSection : MonoBehaviour
    {
        public const string SectionId = "ros-connection";

        /// <summary>The texts on the Settings tab.</summary>
        public const string ChangeLabel = "Change address", CancelLabel = "Cancel", ConnectLabel = "Connect", DeleteLabel = "Del";

        /// <summary>What the user is told when the entry is not an address.</summary>
        public const string InvalidReason = "Not an IP address: enter four numbers from 0 to 255, like 192.168.1.20.";

        private static readonly Vector2 ButtonSize = new Vector2(300f, 56f);
        private static readonly Vector2 KeySize = new Vector2(120f, 56f);
        private static readonly string[] Keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", DeleteLabel };

        private IRosBus bus;
        private IRosEndpoint endpoint;
        private InteractionRouter router;
        private TierUiRig rig;
        private SettingsTab settings;
        private bool lookForSettings;

        // Kept here and not in the controls: the tab is built again whenever a section changes.
        private bool open;
        private string entry = "";

        private TextMeshProUGUI status, readout;
        private string shownHost;
        private int shownPort;
        private bool shownConnected, statusShown;

        /// <summary>Whether the keypad is on show.</summary>
        public bool IsOpen => open;

        /// <summary>What has been entered on the keypad so far.</summary>
        public string Entry => entry;

        /// <summary>The line that says where the app connects and whether it is connected.</summary>
        public static string StatusText(string host, int port, bool connected) =>
            $"{(connected ? "Connected" : "Not connected")} to ROS at {host}:{port}";

        /// <summary>
        /// Put the section on a tier UI's Settings tab, for the connection under
        /// <paramref name="ros"/>. An invalid entry is reported to <paramref name="refusals"/>.
        /// </summary>
        public void Show(IRosBus ros, TierUiRig ui, InteractionRouter refusals)
        {
            if (settings != null) settings.RemoveSection(SectionId);
            settings = null;
            open = false;
            bus = ros;
            endpoint = ros?.Endpoint();
            router = refusals;
            rig = ui;
            lookForSettings = rig != null && endpoint != null;
            TryAddSettings();
        }

        // The rig builds its tiers when it wakes, which may be after the host started.
        private void TryAddSettings()
        {
            if (rig == null) { lookForSettings = false; return; }
            if (rig.Settings == null) return;
            lookForSettings = false;
            settings = rig.Settings;
            settings.AddSection(SectionId, Build);
        }

        private void Update()
        {
            if (lookForSettings) TryAddSettings();
            // The keypad does not wait behind a closed panel or another tab: it is put away
            // when the user leaves it.
            if (open && (readout == null || !readout.gameObject.activeInHierarchy)) Close();
            RefreshStatus();
        }

        private void OnDestroy()
        {
            if (settings != null) settings.RemoveSection(SectionId);
            settings = null;
        }

        private void Build(Transform column)
        {
            status = UiBuilder.CreateLabel("RosStatus", column, "", 24f);
            statusShown = false;
            readout = null;

            // The same button, in the same place, opens the keypad and puts it away.
            UiBuilder.CreateButton("RosAddress", column, open ? CancelLabel : ChangeLabel, ButtonSize, Toggle);
            if (open)
            {
                readout = UiBuilder.CreateLabel("RosAddressEntry", column, "", 32f);
                for (int row = 0; row < Keys.Length / 3; row++)
                {
                    Transform line = UiBuilder.CreateRow($"RosKeys{row + 1}", column).transform;
                    for (int i = 0; i < 3; i++)
                    {
                        string key = Keys[row * 3 + i];
                        UiBuilder.CreateButton(KeyName(key), line, key, KeySize, () => Press(key));
                    }
                }
                UiBuilder.CreateButton("RosConnect", column, ConnectLabel, ButtonSize, Connect);
                var hint = UiBuilder.CreateLabel("RosAddressHint", column,
                    $"The IP address of the computer that runs ROS. The port stays {endpoint.DefaultPort}.", 18f);
                hint.enableWordWrapping = true;
                RefreshEntry();
            }
            RefreshStatus();
        }

        /// <summary>The name of a key's button: <c>RosKey_7</c>, <c>RosKey_dot</c>, <c>RosKey_delete</c>.</summary>
        public static string KeyName(string key) =>
            key == "." ? "RosKey_dot" : key == DeleteLabel ? "RosKey_delete" : $"RosKey_{key}";

        private void Toggle()
        {
            if (open) Close();
            else Open();
        }

        private void Open()
        {
            if (open || settings == null || endpoint == null) return;
            open = true;
            // The address in use, to be corrected: most changes are to its last number.
            entry = endpoint.Host ?? "";
            settings.ShowOnly(SectionId);
        }

        private void Close()
        {
            if (!open) return;
            open = false;
            readout = null;
            if (settings == null) return;
            // Gives the tab back to every section and builds this one again, closed.
            if (settings.Showing == SectionId) settings.ShowOnly(null);
            else settings.AddSection(SectionId, Build);
        }

        private void Press(string key)
        {
            if (key == DeleteLabel)
            {
                if (entry.Length > 0) entry = entry.Substring(0, entry.Length - 1);
            }
            else if (entry.Length < RosAddress.MaxLength) entry += key;
            RefreshEntry();
        }

        private void Connect()
        {
            if (endpoint == null) return;
            if (!RosAddress.TryParseIPv4(entry, out string host))
            {
                Vector3 at = readout != null ? readout.transform.position : transform.position;
                if (router != null) router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.Unspecified, InvalidReason, at));
                return;
            }
            RosEndpointSetting.Save(endpoint, host);
            endpoint.ConnectTo(host, endpoint.DefaultPort);
            Close();
        }

        private void RefreshEntry()
        {
            if (readout != null) readout.text = entry + "_";
        }

        // Written only when it changes: a label that is set every frame is laid out every frame.
        private void RefreshStatus()
        {
            if (status == null || endpoint == null || bus == null) return;
            string host = endpoint.Host;
            int port = endpoint.Port;
            bool connected = bus.HasConnectionThread && !bus.HasConnectionError;
            if (statusShown && connected == shownConnected && port == shownPort && host == shownHost) return;
            statusShown = true;
            shownHost = host;
            shownPort = port;
            shownConnected = connected;
            status.text = StatusText(host, port, connected);
        }
    }
}
