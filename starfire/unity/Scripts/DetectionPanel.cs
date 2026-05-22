using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Starfire
{
    public class DetectionPanel : MonoBehaviour
    {
        public RectTransform Container;
        public GameObject BoxPrefab;           // single pre-instantiated template, hidden
        public int  PoolSize = 8;
        public int  MaxVisible = 5;
        public Color HostileColor  = new Color(1f,    0.2f,  0f,    1f); // #FF3300
        public Color CivilianColor = Color.white;
        public Color VehicleColor  = new Color(1f,    0.667f,0f,    1f); // #FFAA00
        public Color DefaultColor  = new Color(0f,    1f,    0.4f,  1f); // #00FF66

        readonly List<Slot> _pool = new List<Slot>();
        Color _tint = Color.white;

        class Slot
        {
            public GameObject Go;
            public CanvasGroup Cg;
            public RectTransform Rt;
            public Image Box;
            public Image Border;
            public TMP_Text Label;
            public bool Active;
            public float FadeT;       // 0..1
            public bool FadingOut;
            public float PulseT;
            public string ClassCache;
            public string ConfCache;
        }

        void Awake()
        {
            if (BoxPrefab == null || Container == null) return;
            BoxPrefab.SetActive(false);
            for (int i = 0; i < PoolSize; i++)
            {
                var go = Instantiate(BoxPrefab, Container);
                go.SetActive(false);
                var cg = go.GetComponent<CanvasGroup>(); if (!cg) cg = go.AddComponent<CanvasGroup>();
                var rt = go.GetComponent<RectTransform>();
                var images = go.GetComponentsInChildren<Image>(true);
                Image box = images.Length > 0 ? images[0] : null;
                Image border = images.Length > 1 ? images[1] : null;
                var label = go.GetComponentInChildren<TMP_Text>(true);
                _pool.Add(new Slot {
                    Go = go, Cg = cg, Rt = rt, Box = box, Border = border, Label = label });
            }
        }

        public void SetTint(Color c) { _tint = c; }

        void Update()
        {
            var dets = DataStore.Detections;
            int count = dets != null ? Mathf.Min(dets.Count, MaxVisible) : 0;
            int slotIdx = 0;

            for (int i = 0; i < count; i++)
            {
                if (slotIdx >= _pool.Count) break;
                var s = _pool[slotIdx++];
                var d = dets[i];
                Color cl = ColorFor(d.ClassName);

                if (!s.Active)
                {
                    s.Go.SetActive(true);
                    s.Active = true;
                    s.FadeT = 0f;
                    s.FadingOut = false;
                }
                else if (s.FadingOut)
                {
                    s.FadingOut = false;
                }

                // Animate-in fade + scale (0.95 → 1.0 over 0.2s)
                s.FadeT = Mathf.MoveTowards(s.FadeT, 1f, Time.deltaTime / 0.2f);
                s.Cg.alpha = s.FadeT;
                float scale = Mathf.Lerp(0.95f, 1f, s.FadeT);

                // 1Hz pulse if conf > 0.90
                if (d.Confidence > 0.90f)
                {
                    s.PulseT += Time.deltaTime;
                    float pulse = Mathf.Sin(s.PulseT * Mathf.PI * 2f) * 0.5f + 0.5f;
                    if (s.Border) s.Border.color = Color.Lerp(cl, HostileColor, pulse);
                    scale *= 1f + pulse * 0.03f;
                }
                else
                {
                    s.PulseT = 0f;
                    if (s.Border) s.Border.color = cl;
                }
                s.Rt.localScale = new Vector3(scale, scale, 1f);

                if (s.Box) s.Box.color = new Color(cl.r, cl.g, cl.b, 0.2f);

                // SetText only on change
                string conf = $"{Mathf.RoundToInt(d.Confidence * 100f),3}%";
                string cls  = d.ClassName ?? "?";
                if (cls != s.ClassCache || conf != s.ConfCache)
                {
                    if (s.Label) s.Label.text = $"■ {cls,-22} {conf}";
                    s.ClassCache = cls;
                    s.ConfCache  = conf;
                }

                // Vertical stacking
                s.Rt.anchoredPosition = new Vector2(0f, -i * 56f);

                // Locked target outline
                if (DataStore.LockedTargetIndex == i && s.Border)
                {
                    s.Border.color = HostileColor;
                }
            }

            // Fade-out remaining
            for (int i = slotIdx; i < _pool.Count; i++)
            {
                var s = _pool[i];
                if (!s.Active) continue;
                s.FadingOut = true;
                s.FadeT = Mathf.MoveTowards(s.FadeT, 0f, Time.deltaTime / 0.15f);
                s.Cg.alpha = s.FadeT;
                if (s.FadeT <= 0f)
                {
                    s.Go.SetActive(false);
                    s.Active = false;
                    s.ClassCache = null;
                    s.ConfCache = null;
                }
            }
        }

        Color ColorFor(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return DefaultColor;
            if (cls.Contains("HOSTILE") || cls.Contains("WEAPON")) return HostileColor;
            if (cls.Contains("CIVILIAN")) return CivilianColor;
            if (cls.Contains("VEHICLE")) return VehicleColor;
            return DefaultColor;
        }

        public void LockHighestConfidence()
        {
            var dets = DataStore.Detections;
            if (dets == null || dets.Count == 0)
            {
                DataStore.LockedTargetIndex = -1;
                return;
            }
            int best = 0;
            for (int i = 1; i < dets.Count; i++)
                if (dets[i].Confidence > dets[best].Confidence) best = i;
            DataStore.LockedTargetIndex = best;
        }

        public void Clear() => DataStore.LockedTargetIndex = -1;
    }
}
