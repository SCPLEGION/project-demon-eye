using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Starfire
{
    /// <summary>Android SpeechRecognizer + TextToSpeech via AndroidJavaObject.
    /// Wake word ("Astartes" / "Brother" / "Aquila") then 4s command window.
    /// All command outputs go through InputBus.  Editor build is a no-op
    /// but logs simulated events so scenes still work.</summary>
    public class VoiceCommand : MonoBehaviour
    {
        public float CommandWindow = 4f;
        public string PcDomain = "starfire.yourdomain.com";

        enum VState { Standby, WakeDetected, Listening, Processing }
        VState _state = VState.Standby;
        float _windowT;

        static readonly string[] WakeWords  = { "astartes", "brother", "aquila" };
        static readonly string[] HideWords  = { "hide", "cloak" };
        static readonly string[] ShowWords  = { "reveal", "show" };

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _recognizer;
        AndroidJavaObject _tts;
        AndroidJavaObject _activity;
        bool _available;
        bool _voxnetOfflineWarned;
#endif

        void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { InitAndroid(); }
            catch (Exception e) { Debug.LogWarning($"VoiceCommand: init failed {e.Message}"); }
#else
            Debug.Log("VoiceCommand: editor stub (Android-only).");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void InitAndroid()
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

            using (var srClass = new AndroidJavaClass("android.speech.SpeechRecognizer"))
            {
                _available = srClass.CallStatic<bool>("isRecognitionAvailable", _activity);
                if (!_available)
                {
                    if (!_voxnetOfflineWarned)
                    {
                        _voxnetOfflineWarned = true;
                        Debug.LogWarning("VOXNET OFFLINE — GESTURE CONTROL ONLY");
                    }
                    return;
                }
                _recognizer = srClass.CallStatic<AndroidJavaObject>("createSpeechRecognizer", _activity);
            }

            _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech",
                _activity, (AndroidJavaProxy)null);
            _tts.Call<int>("setPitch", 0.6f);
            _tts.Call<int>("setSpeechRate", 0.85f);

            StartListening();
        }

        void StartListening()
        {
            if (_recognizer == null) return;
            using (var intent = new AndroidJavaObject("android.content.Intent",
                "android.speech.action.RECOGNIZE_SPEECH"))
            {
                intent.Call<AndroidJavaObject>("putExtra",
                    "android.speech.extra.LANGUAGE_MODEL", "free_form");
                intent.Call<AndroidJavaObject>("putExtra",
                    "android.speech.extra.PREFER_OFFLINE", true);
                intent.Call<AndroidJavaObject>("putExtra",
                    "android.speech.extra.LANGUAGE", "en-US");
                intent.Call<AndroidJavaObject>("putExtra",
                    "android.speech.extra.PARTIAL_RESULTS", true);
                intent.Call<AndroidJavaObject>("putExtra",
                    "android.speech.extra.MAX_RESULTS", 5);
                _recognizer.Call("setRecognitionListener", new RecognitionListener(this));
                _recognizer.Call("startListening", intent);
            }
        }

        class RecognitionListener : AndroidJavaProxy
        {
            readonly VoiceCommand _owner;
            public RecognitionListener(VoiceCommand owner) : base("android.speech.RecognitionListener")
            { _owner = owner; }

            public void onReadyForSpeech(AndroidJavaObject p) { }
            public void onBeginningOfSpeech() { }
            public void onRmsChanged(float rms) { }
            public void onBufferReceived(AndroidJavaObject buf) { }
            public void onEndOfSpeech() { }

            public void onError(int code)
            {
                // simply restart — common on silence
                _owner.RunOnMain(() => _owner.StartListening());
            }

            public void onPartialResults(AndroidJavaObject bundle)  { Handle(bundle, partial: true); }
            public void onResults(AndroidJavaObject bundle)         { Handle(bundle, partial: false); }
            public void onEvent(int eventType, AndroidJavaObject p) { }

            void Handle(AndroidJavaObject bundle, bool partial)
            {
                if (bundle == null) return;
                try
                {
                    var list = bundle.Call<AndroidJavaObject>("getStringArrayList",
                        "results_recognition");
                    if (list == null) return;
                    int n = list.Call<int>("size");
                    for (int i = 0; i < n; i++)
                    {
                        string spoken = list.Call<string>("get", i);
                        if (!string.IsNullOrEmpty(spoken))
                            _owner.RunOnMain(() => _owner.OnUtterance(spoken.ToLowerInvariant()));
                    }
                }
                catch (Exception e) { Debug.LogWarning($"VoiceCommand result error {e.Message}"); }
                _owner.RunOnMain(() => _owner.StartListening());
            }
        }

        public void Speak(string s)
        {
            if (_tts == null) return;
            try { _tts.Call<int>("speak", "VOXNET: " + s, 0, null, "stf"); }
            catch (Exception e) { Debug.LogWarning($"TTS speak error {e.Message}"); }
        }
#else
        public void Speak(string s) { Debug.Log("VOXNET: " + s); }
#endif

        // ────────────────────────────────────────────────────────────────
        // Cross-thread dispatch
        // ────────────────────────────────────────────────────────────────
        readonly Queue<Action> _mainQueue = new Queue<Action>();
        public void RunOnMain(Action a) { lock (_mainQueue) _mainQueue.Enqueue(a); }

        void Update()
        {
            lock (_mainQueue)
            {
                while (_mainQueue.Count > 0)
                {
                    try { _mainQueue.Dequeue()?.Invoke(); }
                    catch (Exception e) { Debug.LogWarning($"VoiceCommand main-thread error {e.Message}"); }
                }
            }
            if (_state == VState.WakeDetected || _state == VState.Listening)
            {
                _windowT -= Time.deltaTime;
                if (_windowT <= 0f)
                {
                    Speak("STANDING BY");
                    _state = VState.Standby;
                }
            }
        }

        public void OnUtterance(string utterance)
        {
            if (string.IsNullOrWhiteSpace(utterance)) return;

            // wake-word check
            bool hasWake = false;
            int wakeEnd = 0;
            foreach (var w in WakeWords)
            {
                int idx = utterance.IndexOf(w, StringComparison.Ordinal);
                if (idx >= 0) { hasWake = true; wakeEnd = idx + w.Length; break; }
            }

            if (_state == VState.Standby)
            {
                if (!hasWake) return;
                _state = VState.WakeDetected;
                _windowT = CommandWindow;
                InputBus.Instance?.OnGestureFlash?.Invoke("VOXNET ACTIVE");
                // Maybe rest of utterance contains command already
                string tail = utterance.Substring(wakeEnd).Trim();
                if (!string.IsNullOrEmpty(tail)) RouteCommand(tail);
                return;
            }

            // already inside a command window
            RouteCommand(utterance);
        }

        void RouteCommand(string cmd)
        {
            _state = VState.Processing;
            string best = FuzzyPick(cmd, new[]
            {
                "diagnostics","status","augur","network","scan","clear","dismiss",
                "target","lock","hide","cloak","reveal","show","report","mode","emperor"
            });
            switch (best)
            {
                case "diagnostics": case "status":
                    InputBus.Instance?.OnDiagnosticsToggle?.Invoke();
                    Speak("COGITATOR DIAGNOSTICS ACTIVE");
                    break;
                case "augur": case "network": case "scan":
                    InputBus.Instance?.OnAugurToggle?.Invoke();
                    Speak("AUGUR ARRAY ENGAGED");
                    break;
                case "clear": case "dismiss":
                    InputBus.Instance?.OnDismiss?.Invoke();
                    Speak("SLATE CLEARED");
                    break;
                case "target": case "lock":
                    InputBus.Instance?.OnTargetLock?.Invoke();
                    {
                        var dets = DataStore.Detections;
                        int idx = DataStore.LockedTargetIndex;
                        if (dets != null && idx >= 0 && idx < dets.Count)
                            Speak($"TARGET ACQUIRED: {dets[idx].ClassName} " +
                                  $"{Mathf.RoundToInt(dets[idx].Confidence*100)} PERCENT");
                        else
                            Speak("NO TARGET");
                    }
                    break;
                case "hide": case "cloak":
                    DataStore.HUDVisible = false;
                    InputBus.Instance?.OnHudVisibilityToggle?.Invoke();
                    break;
                case "reveal": case "show":
                    DataStore.HUDVisible = true;
                    InputBus.Instance?.OnHudVisibilityToggle?.Invoke();
                    Speak("HUD RESTORED");
                    break;
                case "report":
                    {
                        var dets = DataStore.Detections;
                        if (dets == null || dets.Count == 0) Speak("NO CONTACTS");
                        else
                        {
                            var sb = new System.Text.StringBuilder();
                            sb.Append("DETECTING ");
                            for (int i = 0; i < Mathf.Min(5, dets.Count); i++)
                            {
                                if (i > 0) sb.Append(", ");
                                sb.Append(dets[i].ClassName);
                            }
                            Speak(sb.ToString());
                        }
                        InputBus.Instance?.OnReport?.Invoke();
                    }
                    break;
                case "mode":
                    Speak("NAV LINK " + DataStore.CurrentMode);
                    InputBus.Instance?.OnModeReport?.Invoke();
                    break;
                case "emperor":
                    InputBus.Instance?.OnEmperor?.Invoke();
                    Speak("GLORY TO THE EMPEROR OF MANKIND");
                    break;
                default:
                    // no recognized command
                    break;
            }
            _state = VState.Standby;
            _windowT = 0f;
        }

        static int Levenshtein(string a, string b)
        {
            if (a == b) return 0;
            if (string.IsNullOrEmpty(a)) return b.Length;
            if (string.IsNullOrEmpty(b)) return a.Length;
            int[,] dp = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Mathf.Min(
                        Mathf.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + cost);
                }
            return dp[a.Length, b.Length];
        }

        static string FuzzyPick(string text, string[] vocab)
        {
            string[] tokens = text.Split(new[] { ' ', '\t', ',', '.' },
                StringSplitOptions.RemoveEmptyEntries);
            string best = null;
            int bestDist = int.MaxValue;
            foreach (var tok in tokens)
                foreach (var v in vocab)
                {
                    int d = Levenshtein(tok, v);
                    if (d < bestDist && d <= 2)
                    {
                        bestDist = d;
                        best = v;
                    }
                }
            return best;
        }

        void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { _recognizer?.Call("destroy"); } catch { }
            try { _tts?.Call("shutdown"); } catch { }
#endif
        }
    }
}
