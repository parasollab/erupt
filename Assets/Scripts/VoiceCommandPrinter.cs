using UnityEngine;
using UnityEngine.Windows.Speech;
using System.Collections.Generic;

public class VoiceCommandPrinter : MonoBehaviour
{
    [Header("Listening")]
    [Tooltip("If true, the recognizer will start automatically on Start.")]
    public bool autoStart = true;

    [Tooltip("Key to toggle listening in playmode.")]
    public KeyCode toggleKey = KeyCode.V;

    private DictationRecognizer dictationRecognizer;
    private bool isListening = false;

    private readonly List<string> resultsQueue = new List<string>();
    private readonly object resultsLock = new object();

    void Start()
    {
        InitializeDictationRecognizer();

        if (autoStart)
        {
            StartListening();
        }
    }

    void InitializeDictationRecognizer()
    {
        if (dictationRecognizer != null) return;

        dictationRecognizer = new DictationRecognizer();

        dictationRecognizer.DictationResult += (text, confidence) =>
        {
            EnqueueResult(text);
        };

        dictationRecognizer.DictationHypothesis += (text) =>
        {
            // Optional: you can print hypothesis for immediate feedback
            Debug.Log($"Dictation hypothesis: {text}");
        };

        dictationRecognizer.DictationComplete += (completionCause) =>
        {
            Debug.Log($"Dictation complete: {completionCause}");
            // If completed because of a timeout or network, you may want to restart automatically:
            // if (completionCause == DictationCompletionCause.TimeoutExceeded) StartListening();
        };

        dictationRecognizer.DictationError += (error, hresult) =>
        {
            Debug.LogError($"Dictation error: {error}; HRESULT = {hresult}");
        };
    }

    void EnqueueResult(string text)
    {
        lock (resultsLock)
        {
            resultsQueue.Add(text);
        }
    }

    void Update()
    {
        // Toggle listening with the key
        if (Input.GetKeyDown(toggleKey))
        {
            if (isListening) StopListening();
            else StartListening();
        }

        // Drain the queue on main thread and print results
        List<string> toProcess = null;
        lock (resultsLock)
        {
            if (resultsQueue.Count > 0)
            {
                toProcess = new List<string>(resultsQueue);
                resultsQueue.Clear();
            }
        }

        if (toProcess != null)
        {
            foreach (var text in toProcess)
            {
                Debug.Log($"VoiceCommandPrinter: Recognized -> \"{text}\"");
            }
        }
    }

    public void StartListening()
    {
        if (dictationRecognizer == null) InitializeDictationRecognizer();

        if (!isListening)
        {
            try
            {
                dictationRecognizer.Start();
                isListening = true;
                Debug.Log("VoiceCommandPrinter: Started listening.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"VoiceCommandPrinter: Failed to start dictation recognizer: {ex.Message}");
            }
        }
    }

    public void StopListening()
    {
        if (dictationRecognizer != null && isListening)
        {
            dictationRecognizer.Stop();
            isListening = false;
            Debug.Log("VoiceCommandPrinter: Stopped listening.");
        }
    }

    void OnDisable()
    {
        if (dictationRecognizer != null)
        {
            if (isListening)
            {
                dictationRecognizer.Stop();
                isListening = false;
            }

            dictationRecognizer.DictationResult -= (text, confidence) => EnqueueResult(text);
            dictationRecognizer.Dispose();
            dictationRecognizer = null;
        }
    }
}