using UnityEngine;
using UnityEngine.Events;

namespace Starfire
{
    /// <summary>Single event hub fed by GestureDetector and VoiceCommand,
    /// consumed by HUD panels.  No controller input anywhere.</summary>
    public class InputBus : MonoBehaviour
    {
        public static InputBus Instance { get; private set; }

        public UnityEvent OnDiagnosticsToggle  = new UnityEvent();
        public UnityEvent OnAugurToggle        = new UnityEvent();
        public UnityEvent OnHudVisibilityToggle = new UnityEvent();
        public UnityEvent OnTargetLock         = new UnityEvent();
        public UnityEvent OnPanelCycleNext     = new UnityEvent();
        public UnityEvent OnPanelCyclePrev     = new UnityEvent();
        public UnityEvent OnDismiss            = new UnityEvent();
        public UnityEvent OnAcknowledge        = new UnityEvent();
        public UnityEvent OnEmperor            = new UnityEvent();
        public UnityEvent OnReport             = new UnityEvent();
        public UnityEvent OnModeReport         = new UnityEvent();
        public UnityEvent<string> OnGestureFlash = new UnityEvent<string>();
        public UnityEvent<bool>   OnSignalWeak   = new UnityEvent<bool>();

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // Helper for producers — fires both the action event and the flash
        public void Fire(string gestureName, UnityEvent evt)
        {
            evt?.Invoke();
            OnGestureFlash?.Invoke(gestureName);
        }
    }
}
