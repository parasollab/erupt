using System;
using Erupt.Ui;
using TMPro;
using UnityEngine;

namespace Erupt.Plugins.Ar
{
    /// <summary>The confirmation button beside one proposed robot base.</summary>
    internal sealed class PlacementWidget : IWorldWidget, IDisposable
    {
        private static readonly Vector2 Size = new(200f, 55f);
        private readonly IUiHost host;
        private readonly TextMeshProUGUI label;
        private readonly string namedLabel;

        public string Id { get; }
        public GameObject GameObject { get; }

        public PlacementWidget(IUiHost host, Transform parent, string robotId, Action confirm)
        {
            this.host = host;
            Id = $"ar.place.{robotId}";
            namedLabel = $"Place {robotId} here";
            Canvas canvas = UiBuilder.CreateWorldCanvas(Id, parent, Size);
            GameObject = canvas.gameObject;
            var button = UiBuilder.CreateButton("Confirm", canvas.transform, "Place here", Size, confirm);
            label = button.GetComponentInChildren<TextMeshProUGUI>();
            label.enableAutoSizing = true;
            label.fontSizeMax = 24f;
            label.fontSizeMin = 10f;
            host.RegisterWidget(this);
        }

        public void Follow(Vector3 proposedBase, Camera viewer, bool severalRobots)
        {
            label.text = severalRobots ? namedLabel : "Place here";
            Transform panel = GameObject.transform;
            Vector3 side = Vector3.right;
            if (viewer != null)
            {
                // Use the viewing position so turning the head to aim does not move the button.
                Vector3 across = Vector3.Cross(Vector3.up, proposedBase - viewer.transform.position);
                side = across.sqrMagnitude > 0.0001f ? across.normalized : viewer.transform.right;
            }
            Vector3 target = proposedBase + 0.23f * side + 0.18f * Vector3.up;
            // Leave the hit target still through sub-centimetre marker jitter.
            if ((panel.position - target).sqrMagnitude > 0.01f * 0.01f)
                panel.position = target;
            if (viewer != null)
            {
                Vector3 forward = panel.position - viewer.transform.position;
                if (forward.sqrMagnitude > 0.000001f)
                    panel.rotation = Quaternion.LookRotation(forward, viewer.transform.up);
            }
        }

        public void Dispose()
        {
            host.UnregisterWidget(this);
            if (GameObject == null) return;
            GameObject.SetActive(false);
            UnityEngine.Object.Destroy(GameObject);
        }
    }
}
