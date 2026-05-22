using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Starfire
{
    public class HUDAnimator : MonoBehaviour
    {
        public RawImage Scanlines;             // material uses ScanlineEffect.shader
        public CanvasGroup ScanlinesCg;
        public Graphic[]   CornerBrackets;     // 4 L-shapes
        public CanvasGroup AquilaWatermark;
        public RectTransform ReticleRing;
        public Image        ReticleRingImage;
        public TMP_Text     RangeText;
        public CanvasGroup  FlavorCgA;
        public CanvasGroup  FlavorCgB;
        public TMP_Text     FlavorA;
        public TMP_Text     FlavorB;
        public CanvasGroup  GestureFlashCg;
        public TMP_Text     GestureFlashLabel;

        public Color NormalRing  = new Color(0f,1f,0.4f,1f);
        public Color ThreatRing  = new Color(1f,0.2f,0f,1f);

        Color _tint = Color.white;
        float _flickerTimer;
        float _flickerTarget = 0.1f;
        bool _flavorUsingA = true;
        float _flavorTimer = 0f;
        int _flavorIndex = 0;
        float _gestureFlashT;

        static readonly string[] FullFlavor = {
            "FIDES • ASTARTES • VICTORIAM",
            "PURGE THE HERETIC",
            "FOR THE EMPEROR",
            "MACHINE SPIRIT NOMINAL",
            "COGITATOR ACTIVE",
            "OMNISSIAH GUIDES",
            "DEATH IS NOTHING COMPARED TO VINDICATION",
            "BY HIS WILL WE PURGE",
        };
        static readonly string[] AmberFlavor = {
            "MACHINE SPIRIT DORMANT",
            "RUNNING LOCAL PROTOCOLS",
            "SIGNAL LOST — REMAIN VIGILANT",
            "STAND READY — LINK WILL BE RESTORED",
        };

        public void SetTint(Color c)
        {
            _tint = c;
            if (CornerBrackets != null)
                foreach (var g in CornerBrackets) if (g) g.color = c;
        }

        void OnEnable()
        {
            if (InputBus.Instance != null)
            {
                InputBus.Instance.OnGestureFlash.AddListener(GestureFlash);
                InputBus.Instance.OnEmperor.AddListener(EmperorEasterEgg);
            }
        }

        void OnDisable()
        {
            if (InputBus.Instance != null)
            {
                InputBus.Instance.OnGestureFlash.RemoveListener(GestureFlash);
                InputBus.Instance.OnEmperor.RemoveListener(EmperorEasterEgg);
            }
        }

        void Start()
        {
            if (AquilaWatermark) AquilaWatermark.alpha = 0.05f;
            if (ScanlinesCg) ScanlinesCg.alpha = 0.10f;
            if (FlavorCgA) FlavorCgA.alpha = 0.08f;
            if (FlavorCgB) FlavorCgB.alpha = 0f;
            UpdateFlavor(immediate: true);
        }

        void Update()
        {
            // CRT flicker on scanlines: random ±2% every 0.5–2s
            _flickerTimer -= Time.deltaTime;
            if (_flickerTimer <= 0f)
            {
                _flickerTimer = Random.Range(0.5f, 2.0f);
                _flickerTarget = 0.10f + Random.Range(-0.02f, 0.02f);
            }
            if (ScanlinesCg)
                ScanlinesCg.alpha = Mathf.MoveTowards(ScanlinesCg.alpha, _flickerTarget, Time.deltaTime * 0.5f);

            // Reticle ring
            bool threat = false;
            float bestConf = 0f;
            var dets = DataStore.Detections;
            if (dets != null) for (int i = 0; i < dets.Count; i++) if (dets[i].Confidence > bestConf) bestConf = dets[i].Confidence;
            threat = bestConf > 0.90f;

            float rotSpeed = threat ? 45f : 15f;
            if (ReticleRing) ReticleRing.Rotate(0f, 0f, -rotSpeed * Time.deltaTime);
            if (ReticleRingImage)
            {
                Color baseCol = threat ? ThreatRing : _tint;
                if (threat)
                {
                    float pulse = Mathf.Sin(Time.time * Mathf.PI * 4f) * 0.5f + 0.5f; // 2Hz
                    float s = 1f + pulse * 0.05f;
                    if (ReticleRing) ReticleRing.localScale = new Vector3(s, s, 1f);
                }
                else if (ReticleRing)
                {
                    ReticleRing.localScale = Vector3.one;
                }
                ReticleRingImage.color = baseCol;
            }

            // Range readout
            if (RangeText)
            {
                float closest = DataStore.ClosestObjectDistance;
                string range = closest > 0.7f ? "RANGE: NEAR" :
                               closest > 0.4f ? "RANGE: MID"  :
                               closest > 0.0f ? "RANGE: FAR"  : "";
                if (RangeText.text != range) RangeText.text = range;
            }

            // Flavor text cycle every 8s, crossfade 0.5s
            _flavorTimer += Time.deltaTime;
            if (_flavorTimer >= 8f)
            {
                _flavorTimer = 0f;
                _flavorIndex++;
                StartCoroutine(CrossfadeFlavor());
            }

            // Gesture flash decay
            if (GestureFlashCg && GestureFlashCg.alpha > 0f)
            {
                _gestureFlashT -= Time.deltaTime;
                GestureFlashCg.alpha = Mathf.MoveTowards(GestureFlashCg.alpha, 0f, Time.deltaTime / 0.5f);
            }
        }

        void UpdateFlavor(bool immediate)
        {
            var arr = DataStore.CurrentMode == "FULL" ? FullFlavor : AmberFlavor;
            string txt = arr[((_flavorIndex % arr.Length) + arr.Length) % arr.Length];
            if (_flavorUsingA) { if (FlavorA) FlavorA.text = txt; }
            else                { if (FlavorB) FlavorB.text = txt; }
        }

        IEnumerator CrossfadeFlavor()
        {
            _flavorUsingA = !_flavorUsingA;
            UpdateFlavor(false);
            CanvasGroup fadingIn  = _flavorUsingA ? FlavorCgA : FlavorCgB;
            CanvasGroup fadingOut = _flavorUsingA ? FlavorCgB : FlavorCgA;
            float t = 0f;
            const float dur = 0.5f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = t / dur;
                if (fadingIn)  fadingIn.alpha  = 0.08f * k;
                if (fadingOut) fadingOut.alpha = 0.08f * (1f - k);
                yield return null;
            }
            if (fadingIn)  fadingIn.alpha  = 0.08f;
            if (fadingOut) fadingOut.alpha = 0f;
        }

        public void GestureFlash(string name)
        {
            if (GestureFlashCg)
            {
                GestureFlashCg.alpha = 1f;
                _gestureFlashT = 0.5f;
            }
            if (GestureFlashLabel) GestureFlashLabel.text = name?.ToUpper() ?? "";
        }

        void EmperorEasterEgg()
        {
            StartCoroutine(EmperorPulses());
        }

        IEnumerator EmperorPulses()
        {
            if (GestureFlashLabel) GestureFlashLabel.text = "FOR THE EMPEROR";
            // Imperial March rhythm: 5 long-long-short-short-short pulses over 2s
            float[] beats = { 0.25f,0.25f,0.15f,0.15f,0.15f, 0.25f,0.25f,0.15f,0.15f,0.15f };
            foreach (var dur in beats)
            {
                if (GestureFlashCg) GestureFlashCg.alpha = 1f;
                yield return new WaitForSeconds(dur * 0.5f);
                if (GestureFlashCg) GestureFlashCg.alpha = 0.2f;
                yield return new WaitForSeconds(dur * 0.5f);
            }
            if (GestureFlashCg) GestureFlashCg.alpha = 0f;
            if (GestureFlashLabel) GestureFlashLabel.text = "";
        }
    }
}
