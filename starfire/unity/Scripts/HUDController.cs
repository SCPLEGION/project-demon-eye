using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Starfire
{
    /// <summary>Master HUD controller — polls DataStore every 33ms, drives color
    /// scheme, dispatches panel updates, owns the InputBus subscriptions.</summary>
    public class HUDController : MonoBehaviour
    {
        public Color FullPrimary    = new Color(0f,    1f,    0.4f, 1f); // #00FF66
        public Color FullSecondary  = new Color(0f,    0.2f,  0.07f,1f); // #003311
        public Color AmberPrimary   = new Color(1f,    0.667f,0f,   1f); // #FFAA00
        public Color AmberSecondary = new Color(0.2f,  0.133f,0f,   1f); // #332200

        public CanvasGroup HUDRoot;
        public TMP_Text TopLeftText;     // GPS + vitals
        public TMP_Text TopRightText;    // heading + altitude
        public TMP_Text DepthIndicatorText;
        public TMP_Text FacePanelText;
        public TMP_Text PanelFocusText;
        public Graphic[] PrimaryTinted;  // brackets, reticle ring, etc.
        public Graphic[] SecondaryTinted;

        public DetectionPanel  Detections;
        public CompassPanel    Compass;
        public NavLinkStatus   NavLink;
        public NetworkMapPanel NetworkMap;
        public FPSDebugOverlay FpsOverlay;
        public HUDAnimator     Animator;

        readonly Dictionary<TMP_Text, string> _cache = new Dictionary<TMP_Text, string>();
        readonly string[] _panelOrder = new[]
        {
            "DETECTIONS","COMPASS","NETWORK","DEPTH","FACES","DIAGNOSTICS","FLAVOR"
        };
        int _panelIndex;

        Color _primary, _secondary;
        Color _primaryTarget, _secondaryTarget;
        string _lastMode = "";

        float _pollTimer;
        bool _hudHidden;
        float _hudFade = 1f;

        void Start()
        {
            Application.targetFrameRate = 90;
            QualitySettings.vSyncCount = 0;
            _primary = _primaryTarget   = AmberPrimary;
            _secondary = _secondaryTarget = AmberSecondary;
            ApplyTints();

            if (InputBus.Instance != null)
            {
                InputBus.Instance.OnPanelCycleNext.AddListener(() => CyclePanel(+1));
                InputBus.Instance.OnPanelCyclePrev.AddListener(() => CyclePanel(-1));
                InputBus.Instance.OnHudVisibilityToggle.AddListener(ToggleHUD);
                InputBus.Instance.OnAcknowledge.AddListener(() => SetText(PanelFocusText, "ACKNOWLEDGED — FOR THE EMPEROR"));
            }
        }

        void CyclePanel(int dir)
        {
            _panelIndex = (_panelIndex + dir + _panelOrder.Length) % _panelOrder.Length;
            SetText(PanelFocusText, $"PANEL: {_panelOrder[_panelIndex]}");
        }

        void ToggleHUD()
        {
            _hudHidden = !_hudHidden;
            DataStore.HUDVisible = !_hudHidden;
        }

        void Update()
        {
            _pollTimer -= Time.deltaTime;
            if (_pollTimer > 0f) {
                _hudFade = Mathf.MoveTowards(_hudFade, _hudHidden ? 0f : 1f, Time.deltaTime / 0.3f);
                if (HUDRoot) HUDRoot.alpha = _hudFade;
                LerpColors();
                return;
            }
            _pollTimer = 0.033f;

            // ── pick color targets by mode
            string mode = DataStore.CurrentMode;
            if (mode != _lastMode)
            {
                _lastMode = mode;
                if (mode == "FULL")
                {
                    _primaryTarget   = FullPrimary;
                    _secondaryTarget = FullSecondary;
                }
                else
                {
                    _primaryTarget   = AmberPrimary;
                    _secondaryTarget = AmberSecondary;
                }
            }
            LerpColors();

            // ── HUD top-left vitals
            SetText(TopLeftText,
                $"MODE: {mode}\nLINK: {DataStore.ConnectionStatus}\nLATENCY: {DataStore.WebSocketLatencyMs:F0}ms");

            // ── top-right heading + altitude
            string head = DirectionString(DataStore.Heading);
            SetText(TopRightText,
                $"HDG: {DataStore.Heading:F0}° {head}\nALT: {DataStore.GPS.Altitude:F0}m");

            // ── depth indicator
            float closest = DataStore.ClosestObjectDistance;
            string range = closest > 0.7f ? "NEAR" : closest > 0.4f ? "MID" : "FAR";
            SetText(DepthIndicatorText, $"RANGE: {range}");

            // ── face panel
            if (DataStore.Faces != null && DataStore.Faces.Length > 0)
            {
                var f = DataStore.Faces[0];
                SetText(FacePanelText,
                    $"FACES: {DataStore.Faces.Length}\n" +
                    $"MOUTH: {(f.MouthOpen ? "OPEN" : "CLOSED")}\n" +
                    $"BROW:  {(f.BrowRaised ? "RAISED" : "FLAT")}\n" +
                    $"GAZE:  {f.Position?.ToUpper()}");
            }
            else SetText(FacePanelText, "FACES: NONE");

            _hudFade = Mathf.MoveTowards(_hudFade, _hudHidden ? 0f : 1f, Time.deltaTime / 0.3f);
            if (HUDRoot) HUDRoot.alpha = _hudFade;
        }

        void LerpColors()
        {
            float k = 1f - Mathf.Exp(-Time.deltaTime * 4f); // ~0.5s
            _primary   = Color.Lerp(_primary,   _primaryTarget,   k);
            _secondary = Color.Lerp(_secondary, _secondaryTarget, k);
            ApplyTints();
        }

        void ApplyTints()
        {
            if (PrimaryTinted != null)
                foreach (var g in PrimaryTinted) if (g) g.color = _primary;
            if (SecondaryTinted != null)
                foreach (var g in SecondaryTinted) if (g) g.color = _secondary;
            if (Detections) Detections.SetTint(_primary);
            if (NavLink)    NavLink.SetTint(_primary, _secondary);
            if (Compass)    Compass.SetTint(_primary);
            if (Animator)   Animator.SetTint(_primary);
        }

        public Color Primary   => _primary;
        public Color Secondary => _secondary;

        void SetText(TMP_Text label, string value)
        {
            if (label == null) return;
            if (_cache.TryGetValue(label, out var prev) && prev == value) return;
            _cache[label] = value;
            label.text = value;
        }

        static string DirectionString(float bearing)
        {
            bearing = ((bearing % 360f) + 360f) % 360f;
            string[] dirs = { "N","NE","E","SE","S","SW","W","NW" };
            int idx = (int)Mathf.Round(bearing / 45f) % 8;
            return dirs[idx];
        }
    }
}
