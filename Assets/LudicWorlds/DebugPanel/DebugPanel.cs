using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;
using System.Collections.Generic;
using System.Text;


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
        private static readonly Queue<string> _lines = new Queue<string>();
        private static int _lineChars;
        private float _timeSinceFlush;

        private const int MAX_LINES = 500;
        // Legacy UI.Text stops rendering past 65000 verts (~16k glyphs); stay well under.
        private const int MAX_CHARS = 12000;
        private const int MAX_LINE_LENGTH = 1000;
        private const float FLUSH_INTERVAL = 0.25f;

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
            _cameraTransform = Camera.main?.transform;
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
                if (_fpsSamples > 0)
                {
                    _fpsText.text = (Mathf.Round(_sumFps / _fpsSamples)).ToString();
                }

                _elapsedTime = 0f;
                _sumFps = 0f;
                _fpsSamples = 0;
            }

            _sumFps += (1.0f / Mathf.Max(Time.smoothDeltaTime, 0.0001f));
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

            // Flush at a fixed cadence, not per message: per-frame log spam (e.g. an
            // exception thrown every Update) would otherwise force a full canvas
            // rebuild every frame and halve the framerate.
            _timeSinceFlush += Time.deltaTime;
            if (_debugText != null && _queuedMessages.Count > 0 && _timeSinceFlush >= FLUSH_INTERVAL)
            {
                _timeSinceFlush = 0f;
                FlushMessages();
            }
        }

        private void FlushMessages()
        {
            while (_queuedMessages.Count > 0)
            {
                string msg = _queuedMessages.Dequeue();

                int repeats = 1;
                while (_queuedMessages.Count > 0 && _queuedMessages.Peek() == msg)
                {
                    _queuedMessages.Dequeue();
                    repeats++;
                }

                if (msg.Length > MAX_LINE_LENGTH)
                    msg = msg.Substring(0, MAX_LINE_LENGTH) + "…";
                if (repeats > 1)
                    msg += $"  (x{repeats})";

                _lines.Enqueue(msg);
                _lineChars += msg.Length + 1;
            }

            while (_lines.Count > 1 && (_lines.Count > MAX_LINES || _lineChars > MAX_CHARS))
                _lineChars -= _lines.Dequeue().Length + 1;

            var sb = new StringBuilder(_lineChars);
            foreach (string line in _lines)
                sb.Append(line).Append('\n');
            _debugText.text = sb.ToString();

            ScrollToBottom();
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
            _lines.Clear();
            _lineChars = 0;
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

    }
}
