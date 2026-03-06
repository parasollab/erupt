using UnityEngine;
using TMPro;
using Vosk;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine.Networking;
using System.Collections;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using System;

using System.Collections;

//[RequireComponent(typeof(ARPlaneManager))]
public class VoiceActivation : MonoBehaviour
{
    [SerializeField]
    private InputActionReference _togglePlanesAction;
    public string modelName = "vosk-model-small-en-us-0.15";


    //public TextMeshProUGUI speechText;
    //public TextMeshProUGUI btnText;
    Dictionary<string, Action> commandActions;
    List<string> keys;


    private Model model;
    bool modelReady = false;
    private VoskRecognizer recognizer;
    private AudioClip micClip;
    private string micDevice;
    private int sampleRate = 16000;
    private bool isListening = false;
    private int lastSamplePosition = 0;
    IEnumerator CopyModelAndroid()
    {
        if (modelReady)
        {
            Debug.Log("Model already loaded");
            yield break;
        }
        string sourceRoot = Path.Combine(Application.streamingAssetsPath, modelName);
        string targetRoot = Path.Combine(Application.persistentDataPath, modelName);
        string fileListPath = Path.Combine(sourceRoot, "filelist.txt");
        string filetargetRoot = Path.Combine(targetRoot, "filelist.txt");

        if (Directory.Exists(targetRoot))
        {
            if (File.Exists(filetargetRoot))
            {
                LoadAndroidModel(targetRoot);
                yield break;

            }
            Debug.Log("Model already exists, loading...");
        }

        Directory.CreateDirectory(targetRoot);

        // Load file list

        string fileListText;

        using (UnityWebRequest uwr = UnityWebRequest.Get(fileListPath))
        {
            yield return uwr.SendWebRequest();
            Debug.LogError("Reading to read filelist.txt-->" + fileListPath);
            if (uwr.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Failed to read filelist.txt");
                yield break;
            }
            fileListText = uwr.downloadHandler.text;
        }

        string[] files = fileListText.Split('\n');

        foreach (string file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;

            string src = Path.Combine(sourceRoot, file.Trim());
            string dst = Path.Combine(targetRoot, file.Trim());

            Debug.LogError("Copying file: " + file + "src=" + src + ":dst=" + dst);

            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            Debug.LogError("Dir created file: " + Path.GetDirectoryName(dst));

            if (File.Exists(src))
            {
                Debug.Log("File already exists, skipping: " + src);
            }
            else
            {
                Debug.LogError("Source file not found: " + src);
            }

            using (UnityWebRequest uwr = UnityWebRequest.Get(src))
            {
                yield return uwr.SendWebRequest();
                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("src file not found at " + File.Exists(src) + ":::::" + src);
                    Debug.LogError("dst directory not found at " + Directory.Exists(Path.GetDirectoryName(dst)) + ":::::::" + Path.GetDirectoryName(dst));
                    Debug.LogError("Failed to copy: " + file + "src=" + src + ":dst=" + dst + "uwr :" + uwr + "error=" + uwr.error);
                    yield break;
                }
                File.WriteAllBytes(dst, uwr.downloadHandler.data);
            }

            Debug.Log("VoiceApp Copied: " + file);
        }

        Debug.Log("Model copied successfully");
        LoadAndroidModel(targetRoot);
    }

    void LoadAndroidModel(string path)
    {
        model = new Model(path);
        recognizer = new VoskRecognizer(model, sampleRate);
        modelReady = true;
        Debug.Log("Vosk model ready (Android)");
    }

    void LoadModelWindows()
    {
        //speechText.text += "At 0!";
        // Pick first available microphone
        if (Microphone.devices.Length == 0)
        {
            //speechText.text += "NMF!";
            Debug.LogError("No microphone found!");
            return;
        }
        //speechText.text += "At 1!";

        micDevice = Microphone.devices[0];

        ///speechText.text += "At 2!";
        // Load Vosk model
        string modelPath = Path.Combine(
            Application.streamingAssetsPath,
            "vosk-model-small-en-us-0.15"
        );
        //speechText.text += "At mp"+modelPath;
        try
        {
            model = new Model(modelPath);
            //          speechText.text += "At 3!";
            recognizer = new VoskRecognizer(model, sampleRate);
            //          speechText.text += "At 4!";
        }
        catch (System.Exception ex)
        {
            Debug.Log("ERROR: " + ex.Message);
            Debug.LogError("Model/Recognizer Error: " + ex.ToString());
            return;
        }
        //speechText.text += "VR="+Microphone.devices.Length;
        modelReady = true;
        Debug.Log("Vosk ready");
    }


    void Start()
    {
        commandActions = new Dictionary<string, Action> {
            {"Stop Listening", () => StartListening()},
            {"stop Listening", () => StartListening()},

            {"Add Shape", () => AddShape()},
            {"add Shape", () => AddShape()}
        };
        keys = new List<string>(commandActions.Keys);

#if UNITY_ANDROID
        StartCoroutine(CopyModelAndroid());
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
               UnityEngine.Android.Permission.Microphone))
        {
            UnityEngine.Android.Permission.RequestUserPermission(
                UnityEngine.Android.Permission.Microphone);
        }
#else
        LoadModelWindows();
#endif

        _togglePlanesAction.action.performed += onTogglePlanesAction;
        // _planeManager.planesChanged += OnPlanesChanged;


    }

    private void onTogglePlanesAction(InputAction.CallbackContext obj)
    {
        if (obj.performed)
        {
            StartListening();
        }
    }
    public void AddShape()
    {
        Debug.Log("AddShape Called! YAYYYYYYYYYYY");
        return;
        //Code to be writtent hereto pass the call to actual add shape code in another script
    }

    public void StartListening()
    {
        Debug.Log("At 0!");

        if (modelReady == false)
        {
            Debug.Log("Vosk model not ready");
            return;
        }



        Debug.Log("Vosk ready");

        if (isListening)
        {
            Debug.Log("Listening stopped");
            Debug.Log("Listening stopped");
            Microphone.End(micDevice);
            Debug.Log("Start Listening");
            isListening = false;

        }
        else
        {
            Debug.Log("Listening started");
            Debug.Log("Listening...");

            // This is workaround to fix issue with first time now working
            //idk y but first time it doesn't work, so we start and stop immediately to fix it
            //debug lol

            micClip = Microphone.Start(micDevice, true, 10, sampleRate);
            // start reading from current position to avoid consuming initial silence
            lastSamplePosition = Microphone.GetPosition(micDevice);
            Microphone.End(micDevice);
            //end of workaround

            micClip = Microphone.Start(micDevice, true, 10, sampleRate);
            // start reading from current position to avoid consuming initial silence
            lastSamplePosition = Microphone.GetPosition(micDevice);
            //speechText.text += "\nClip freq=" + micClip.frequency + " ch=" + micClip.channels + " samples=" + micClip.samples;
            Debug.Log("Button text set to Stop Listening");
            isListening = true;

        }

    }
    int count = 0;

    void Update()
    {

        if (!isListening || micClip == null) return;
        Debug.Log("Update 1");

        int currentPosition = Microphone.GetPosition(micDevice);
        bool isRec = Microphone.IsRecording(micDevice);

        if (currentPosition == lastSamplePosition) return;

        int sampleCount = 0;
        float[] samples = null;
        Debug.Log("Update 2");
        // Handle circular buffer wrap-around from Microphone
        if (currentPosition > lastSamplePosition)
        {
            sampleCount = currentPosition - lastSamplePosition;
            if (sampleCount > 0)
            {
                samples = new float[sampleCount];
                micClip.GetData(samples, lastSamplePosition);
            }
        }
        else // wrapped
        {
            int samplesToEnd = micClip.samples - lastSamplePosition;
            sampleCount = samplesToEnd + currentPosition;
            if (sampleCount > 0)
            {
                samples = new float[sampleCount];
                float[] part1 = new float[samplesToEnd];
                float[] part2 = new float[currentPosition];
                micClip.GetData(part1, lastSamplePosition);
                micClip.GetData(part2, 0);
                part1.CopyTo(samples, 0);
                part2.CopyTo(samples, samplesToEnd);
            }
        }
        Debug.Log("Update 3");
        if (samples == null || sampleCount <= 0)
        {
            lastSamplePosition = currentPosition;
            return;
        }

        // occasional lightweight diagnostics
        if ((count & 0x3F) == 0) // every 64 updates
        {
            //  speechText.text += "\nDBG pos=" + currentPosition + " last=" + lastSamplePosition + " rec=" + isRec + " samples=" + micClip.samples;
        }
        Debug.Log("Update 4");
        lastSamplePosition = currentPosition;

        byte[] buffer = new byte[samples.Length * 2];
        int index = 0;

        foreach (float sample in samples)
        {
            //speechText.text += "At 1!";
            short s = (short)(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue);
            buffer[index++] = (byte)(s & 0xff);
            buffer[index++] = (byte)((s >> 8) & 0xff);
        }
        Debug.Log("Update 5");
        if (recognizer.AcceptWaveform(buffer, buffer.Length))
        {
            string result = recognizer.Result();
            //speechText.text += "UpdateText: " +result;
            UpdateText(result);
        }
        else
        {
            string partial = recognizer.PartialResult();
            UpdateText(partial);
        }
    }

    void UpdateText(string json)
    {

        if (string.IsNullOrEmpty(json)) return;
        //  speechText.text += "UpdateText: " +json;

        if (json.Contains("\"text\""))
        {
            int start = json.IndexOf(":") + 2;
            int end = json.LastIndexOf("\"");
            Debug.Log("UpdateText: " + json.Substring(start, end - start));
            string command = json.Substring(start, end - start);
            //bad code making list everytime we speek. fix later if needed, but this is just a demo so whatever


            for (int i = 0; i < commandActions.Count; i++)
            {
                if (command.ToLower().Contains(keys[i].ToLower()))
                {
                    commandActions[keys[i]].Invoke();
                    break;
                }
            }
        }
    }

    void OnDestroy()
    {
        // Stop listening and flush final recognition result
        if (Microphone.IsRecording(micDevice))
            Microphone.End(micDevice);
        recognizer?.Dispose();
        model?.Dispose();
    }
}
