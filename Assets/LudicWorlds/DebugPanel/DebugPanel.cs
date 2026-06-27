using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;
using System.Collections.Generic;


namespace LudicWorlds
{
    public class DebugPanel : MonoBehaviour
    {
        private static Canvas   _canvas;
        private static Text     _debugText;
        private static Text     _fpsText;
        private static Text     _statusText;

        private float   _elapsedTime;
        private uint    _fpsSamples;
        private float   _sumFps;

        private Queue<string> _queuedMessages;

        private const int MAX_LINES = 500;

        private bool _billboardEnabled = true;
        private Transform _cameraTransform;
        private Vector3 _dirToPlayer = Vector3.zero;
        private ScrollRect _scrollRect;

        void Awake()
        {
            // Register log capture first so exceptions in setup are themselves visible.
            _queuedMessages = new Queue<string>();
            Application.logMessageReceived += OnMessageReceived;

            AcquireObjects();
            SetupScrollRect();
            SetupGrab();

            _elapsedTime = 0;
            _fpsSamples = 0;
            _fpsText.text = "0";
        }

        void Start()
        {
            _cameraTransform = Camera.main.transform;
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnMessageReceived;
        }

        private void AcquireObjects()
        {
            _canvas = GetComponent<Canvas>();
            Transform ui = transform.Find("UI");

            _debugText  = ui.Find("DebugText").GetComponent<Text>();
            _fpsText    = ui.Find("FpsText").GetComponent<Text>();
            _statusText = ui.Find("StatusText").GetComponent<Text>();
        }

        private void SetupScrollRect()
        {
            Transform ui = transform.Find("UI");
            RectTransform debugTextRT = _debugText.GetComponent<RectTransform>();

            // Create a Viewport that sits in the same rect as the original DebugText.
            // RectMask2D clips by rect bounds without stencil — more reliable than Mask+Image.
            var viewportGO = new GameObject("Viewport");
            viewportGO.layer = gameObject.layer;
            var viewportRT = viewportGO.AddComponent<RectTransform>();
            viewportGO.AddComponent<RectMask2D>();

            viewportRT.SetParent(ui, false);
            viewportRT.anchorMin        = debugTextRT.anchorMin;
            viewportRT.anchorMax        = debugTextRT.anchorMax;
            viewportRT.anchoredPosition = debugTextRT.anchoredPosition;
            viewportRT.sizeDelta        = debugTextRT.sizeDelta;
            viewportRT.pivot            = debugTextRT.pivot;

            // Move DebugText under Viewport; anchor to top so it grows downward.
            debugTextRT.SetParent(viewportRT, false);
            debugTextRT.anchorMin        = new Vector2(0f, 1f);
            debugTextRT.anchorMax        = new Vector2(1f, 1f);
            debugTextRT.pivot            = new Vector2(0.5f, 1f);
            debugTextRT.anchoredPosition = Vector2.zero;
            debugTextRT.sizeDelta        = Vector2.zero;

            _debugText.verticalOverflow   = VerticalWrapMode.Overflow;
            _debugText.horizontalOverflow = HorizontalWrapMode.Wrap;

            var csf = _debugText.gameObject.AddComponent<ContentSizeFitter>();
            csf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            _scrollRect = ui.gameObject.AddComponent<ScrollRect>();
            _scrollRect.content      = debugTextRT;
            _scrollRect.viewport     = viewportRT;
            _scrollRect.horizontal   = false;
            _scrollRect.vertical     = true;
            _scrollRect.inertia      = false;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 30f;
        }

        private void SetupGrab()
        {
            var rb = gameObject.AddComponent<Rigidbody>();
            rb.useGravity  = false;
            rb.isKinematic = true;

            // Canvas is 640×480 local units; BoxCollider size is in the same local space.
            var col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(640f, 480f, 10f);

            var grab = gameObject.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.selectEntered.AddListener(_ => _billboardEnabled = false);
            grab.selectExited.AddListener(_ => _billboardEnabled = true);

            // Replace the standard GraphicRaycaster with the XR-aware one so the
            // controller ray can drive the ScrollRect via the thumbstick scroll axis.
            var standardRaycaster = GetComponent<GraphicRaycaster>();
            if (standardRaycaster != null) Destroy(standardRaycaster);
            gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
        }

        void OnMessageReceived(string message, string stackTrace, LogType type)
        {
            _queuedMessages.Enqueue(message);
        }

        void Update()
        {
            _elapsedTime += Time.deltaTime;

            if (_elapsedTime > 0.5f)
            {
                _fpsText.text = (Mathf.Round(_sumFps / _fpsSamples)).ToString();
                _elapsedTime = 0f;
                _sumFps = 0f;
                _fpsSamples = 0;
            }

            _sumFps += (1.0f / Time.smoothDeltaTime);
            _fpsSamples++;

            if (_billboardEnabled)
            {
                if (_cameraTransform == null) _cameraTransform = Camera.main?.transform;
                if (_cameraTransform != null)
                {
                    _dirToPlayer = (transform.position - _cameraTransform.position).normalized;
                    _dirToPlayer.y = 0;
                    transform.rotation = Quaternion.LookRotation(_dirToPlayer);
                }
            }

            if (_debugText != null && _queuedMessages.Count > 0)
            {
                while (_queuedMessages.Count > 0)
                    _debugText.text += _queuedMessages.Dequeue() + "\n";

                TrimText();
                ScrollToBottom();
            }
        }

        private void ScrollToBottom()
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_debugText.rectTransform);
            if (_scrollRect != null)
                _scrollRect.normalizedPosition = new Vector2(0f, 0f);
        }

        public static void Clear()
        {
            if (_debugText is null) return;
            _debugText.text = "";
        }

        public static void Show()
        {
            SetVisibility(true);
        }

        public static void Hide()
        {
            SetVisibility(false);
        }

        public static void SetVisibility(bool visible)
        {
            if (_canvas is null) return;
            _canvas.enabled = visible;
        }

        public static void ToggleVisibility()
        {
            if (_canvas is null) return;
            _canvas.enabled = !_canvas.enabled;
        }

        public static void SetStatus(string message)
        {
            if (_statusText is null) return;
            _statusText.text = message;
        }

        private static void TrimText()
        {
            string[] lines = _debugText.text.Split('\n');
            if (lines.Length > MAX_LINES)
                _debugText.text = string.Join("\n", lines, lines.Length - MAX_LINES, MAX_LINES);
        }
    }
}
