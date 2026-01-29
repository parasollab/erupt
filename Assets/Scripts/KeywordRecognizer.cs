using UnityEngine;
using UnityEngine.Windows.Speech;
using System.Collections.Generic;

public class KeywordRecognizer : MonoBehaviour
{
    private UnityEngine.Windows.Speech.KeywordRecognizer recognizer;
    private Dictionary<string, System.Action> keywords = new Dictionary<string, System.Action>();

    private void Start()
    {
        // Define the keywords and their associated actions
        keywords.Add("Hello", OnHelloKeyword);

        // Create the keyword recognizer with the keywords
        recognizer = new UnityEngine.Windows.Speech.KeywordRecognizer(keywords.Keys);

        // Subscribe to the OnPhraseRecognized event
        recognizer.OnPhraseRecognized += OnPhraseRecognized;

        // Start the recognizer
        recognizer.Start();

        Debug.Log("KeywordRecognizer started. Say 'Hello' to trigger the command.");
    }

    private void OnPhraseRecognized(PhraseRecognizedEventArgs args)
    {
        // Execute the action associated with the recognized keyword
        if (keywords.ContainsKey(args.text))
        {
            keywords[args.text].Invoke();
        }
    }

    private void OnHelloKeyword()
    {
        Debug.Log("Hello World");
        // You can also print to console or perform other actions here
        print("Hello World");
    }

    private void OnDestroy()
    {
        // Stop and dispose of the recognizer
        if (recognizer != null)
        {
            recognizer.OnPhraseRecognized -= OnPhraseRecognized;
            recognizer.Stop();
            recognizer.Dispose();
        }
    }
}
