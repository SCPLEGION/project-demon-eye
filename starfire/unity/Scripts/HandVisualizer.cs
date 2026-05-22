using TMPro;
using UnityEngine;

namespace Starfire
{
    [RequireComponent(typeof(OVRHand))]
    public class HandVisualizer : MonoBehaviour
    {
        public OVRHand Hand;
        public SkinnedMeshRenderer HandRenderer;
        public TMP_Text UndetectedLabel;
        public Color OnlineColor   = new Color(0f,    1f,    0.4f, 0.4f);
        public Color WeakColor     = new Color(1f,    0.667f,0f,   0.4f);
        public float PulseDuration = 0.3f;

        MaterialPropertyBlock _mpb;
        float _pulseT;
        bool _wasTracked = true;
        float _missingT;

        void Reset() { Hand = GetComponent<OVRHand>(); }

        void Awake()
        {
            if (Hand == null) Hand = GetComponent<OVRHand>();
            _mpb = new MaterialPropertyBlock();
            if (UndetectedLabel) UndetectedLabel.gameObject.SetActive(false);
        }

        void OnEnable()
        {
            if (InputBus.Instance != null)
                InputBus.Instance.OnGestureFlash.AddListener(OnGestureFlash);
        }

        void OnDisable()
        {
            if (InputBus.Instance != null)
                InputBus.Instance.OnGestureFlash.RemoveListener(OnGestureFlash);
        }

        void OnGestureFlash(string _) { _pulseT = PulseDuration; }

        void Update()
        {
            if (Hand == null || HandRenderer == null) return;

            bool tracked = Hand.IsTracked;
            bool weak = tracked && Hand.HandConfidence != OVRHand.TrackingConfidence.High;
            HandRenderer.enabled = tracked && Hand.HandConfidence != OVRHand.TrackingConfidence.Low;

            Color baseCol = weak ? WeakColor : OnlineColor;
            if (_pulseT > 0f)
            {
                _pulseT -= Time.deltaTime;
                float k = Mathf.Clamp01(_pulseT / PulseDuration);
                baseCol = new Color(baseCol.r, baseCol.g, baseCol.b,
                    Mathf.Lerp(baseCol.a, 1f, k));
            }

            HandRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor("_Color",      baseCol);
            _mpb.SetColor("_BaseColor",  baseCol);
            _mpb.SetColor("_OutlineColor", baseCol);
            HandRenderer.SetPropertyBlock(_mpb);

            // UNDETECTED toast
            if (tracked != _wasTracked)
            {
                _wasTracked = tracked;
                if (!tracked)
                {
                    if (UndetectedLabel)
                    {
                        UndetectedLabel.gameObject.SetActive(true);
                        UndetectedLabel.text = "GAUNTLET: UNDETECTED";
                    }
                    _missingT = 2f;
                }
            }
            if (_missingT > 0f)
            {
                _missingT -= Time.deltaTime;
                if (_missingT <= 0f && UndetectedLabel)
                    UndetectedLabel.gameObject.SetActive(false);
            }
        }
    }
}
