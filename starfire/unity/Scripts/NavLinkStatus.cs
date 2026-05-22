using System.Collections;
using TMPro;
using UnityEngine;

namespace Starfire
{
    public class NavLinkStatus : MonoBehaviour
    {
        public TMP_Text MainLabel;
        public TMP_Text FlashLabel;        // overlaid flash text during transitions
        public CanvasGroup MainCg;
        public string PcDomain = "starfire.yourdomain.com";

        public Color FullColor     = new Color(0f,    1f,    0.4f, 1f);
        public Color AmberColor    = new Color(1f,    0.667f,0f,   1f);

        Color _primary;
        Color _secondary;
        string _lastMode = "";
        string _lastConn = "";
        Coroutine _activeTransition;
        float _dotTimer;
        int _dotPhase;

        public void SetTint(Color primary, Color secondary)
        {
            _primary = primary;
            _secondary = secondary;
            if (MainLabel) MainLabel.color = primary;
        }

        void Start()
        {
            if (FlashLabel) FlashLabel.text = "";
            if (MainCg) MainCg.alpha = 1f;
        }

        void Update()
        {
            string mode = DataStore.CurrentMode;
            string conn = DataStore.ConnectionStatus;

            if (mode != _lastMode || conn != _lastConn)
            {
                bool wasFull = _lastMode == "FULL";
                bool nowFull = mode == "FULL";

                if (wasFull && !nowFull && !string.IsNullOrEmpty(_lastMode))
                {
                    if (_activeTransition != null) StopCoroutine(_activeTransition);
                    _activeTransition = StartCoroutine(TransitionFullToFallback());
                }
                else if (!wasFull && nowFull && !string.IsNullOrEmpty(_lastMode))
                {
                    if (_activeTransition != null) StopCoroutine(_activeTransition);
                    _activeTransition = StartCoroutine(TransitionFallbackToFull());
                }
                else
                {
                    SetStatic();
                }
                _lastMode = mode;
                _lastConn = conn;
            }

            // animated dots while CONNECTING
            if (conn == "CONNECTING" || conn == "RECONNECTING")
            {
                _dotTimer += Time.deltaTime;
                if (_dotTimer >= 0.5f)   // 0.5Hz advance
                {
                    _dotTimer = 0f;
                    _dotPhase = (_dotPhase + 1) % 4;
                    if (MainLabel)
                        MainLabel.text = "ESTABLISHING NAV LINK" + new string('●', _dotPhase);
                }
            }
        }

        void SetStatic()
        {
            if (MainLabel == null) return;
            switch (DataStore.CurrentMode)
            {
                case "FULL":
                    MainLabel.text = $"● COGITATOR LINK: {PcDomain}";
                    MainLabel.color = FullColor;
                    break;
                case "FALLBACK":
                    MainLabel.text = "⚠ COGITATOR LINK: LOCAL ONLY";
                    MainLabel.color = AmberColor;
                    break;
                default:
                    MainLabel.text = "✕ COGITATOR OFFLINE — EDGE PROTOCOLS ACTIVE";
                    MainLabel.color = AmberColor;
                    break;
            }
            if (MainCg) MainCg.alpha = 1f;
            if (FlashLabel) FlashLabel.text = "";
        }

        IEnumerator TransitionFullToFallback()
        {
            // 1.5s total: flicker 3 flashes (0..0.6), color shift 0.8..1.0, flash text 1.0..1.4
            float t = 0f;
            // 3 rapid flashes spanning 0..0.6
            float flashEnd = 0.6f;
            while (t < flashEnd)
            {
                float phase = (t / flashEnd) * Mathf.PI * 6f; // 3 cycles
                if (MainCg) MainCg.alpha = (Mathf.Cos(phase) * 0.5f + 0.5f) * 0.8f + 0.2f;
                t += Time.deltaTime;
                yield return null;
            }
            if (MainCg) MainCg.alpha = 1f;
            // color shift 0.8..1.0 (0.2s)
            Color start = MainLabel ? MainLabel.color : FullColor;
            float ct = 0f;
            while (ct < 0.2f)
            {
                if (MainLabel) MainLabel.color = Color.Lerp(start, AmberColor, ct / 0.2f);
                ct += Time.deltaTime;
                yield return null;
            }
            if (MainLabel) MainLabel.color = AmberColor;
            // flash text at 1.0..1.4
            if (FlashLabel) {
                FlashLabel.color = AmberColor;
                FlashLabel.text = "SIGNAL LOST — ENGAGING LOCAL PROTOCOLS";
            }
            yield return new WaitForSeconds(0.4f);
            if (FlashLabel) FlashLabel.text = "";
            SetStatic();
            _activeTransition = null;
        }

        IEnumerator TransitionFallbackToFull()
        {
            // 0.5s total: pulse at t=0, color shift 0.2..0.3, flash text 0.3..0.5
            if (MainCg) MainCg.alpha = 1f;
            // single bright pulse — overshoot scale 1.0 → 1.1 → 1.0 in 0.2s
            float pt = 0f;
            Vector3 baseScale = MainLabel ? MainLabel.transform.localScale : Vector3.one;
            while (pt < 0.2f)
            {
                float k = Mathf.Sin((pt / 0.2f) * Mathf.PI);
                if (MainLabel) MainLabel.transform.localScale = baseScale * (1f + 0.1f * k);
                pt += Time.deltaTime;
                yield return null;
            }
            if (MainLabel) MainLabel.transform.localScale = baseScale;
            // color shift to green over 0.1s
            Color start = MainLabel ? MainLabel.color : AmberColor;
            float ct = 0f;
            while (ct < 0.1f)
            {
                if (MainLabel) MainLabel.color = Color.Lerp(start, FullColor, ct / 0.1f);
                ct += Time.deltaTime;
                yield return null;
            }
            if (MainLabel) MainLabel.color = FullColor;
            if (FlashLabel) {
                FlashLabel.color = FullColor;
                FlashLabel.text = "COGITATOR LINK RESTORED — OMNISSIAH GUIDES";
            }
            yield return new WaitForSeconds(0.2f);
            if (FlashLabel) FlashLabel.text = "";
            SetStatic();
            _activeTransition = null;
        }
    }
}
