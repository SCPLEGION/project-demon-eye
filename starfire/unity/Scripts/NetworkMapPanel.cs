using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Starfire
{
    public class NetworkMapPanel : MonoBehaviour
    {
        public CanvasGroup Cg;
        public RectTransform Container;
        public GameObject RowPrefab;        // hidden template: Dot Image + TMP_Text
        public int PoolSize = 8;
        public Color OnlineColor   = new Color(0f,1f,0.4f,1f);
        public Color DegradedColor = new Color(1f,0.667f,0f,1f);
        public Color LostColor     = new Color(1f,0.2f,0f,1f);

        readonly List<Row> _pool = new List<Row>();
        readonly Dictionary<string, string> _seen = new Dictionary<string, string>(); // ip→nodeId
        readonly HashSet<string> _nowKeys = new HashSet<string>();
        readonly List<string> _vanishedScratch = new List<string>();
        bool _visible;
        float _alpha;

        class Row
        {
            public GameObject Go;
            public Image Dot;
            public TMP_Text Label;
            public string IpCache;
            public float Flash;       // 0..1, color flash on appearance/loss
            public Color FlashColor;
            public bool FadingOut;
            public float FadeT;
        }

        void Awake()
        {
            if (RowPrefab == null || Container == null) return;
            RowPrefab.SetActive(false);
            for (int i = 0; i < PoolSize; i++)
            {
                var go = Instantiate(RowPrefab, Container);
                go.SetActive(false);
                var dot = go.GetComponentInChildren<Image>(true);
                var lbl = go.GetComponentInChildren<TMP_Text>(true);
                _pool.Add(new Row { Go = go, Dot = dot, Label = lbl });
            }
            if (Cg) Cg.alpha = 0f;
        }

        void OnEnable()
        {
            if (InputBus.Instance != null)
                InputBus.Instance.OnAugurToggle.AddListener(Toggle);
        }

        void OnDisable()
        {
            if (InputBus.Instance != null)
                InputBus.Instance.OnAugurToggle.RemoveListener(Toggle);
        }

        public void Toggle() { _visible = !_visible; }

        void Update()
        {
            _alpha = Mathf.MoveTowards(_alpha, _visible ? 1f : 0f, Time.deltaTime / 0.2f);
            if (Cg) Cg.alpha = _alpha;
            if (_alpha <= 0f) return;

            var map = DataStore.NetworkMap ?? new NetworkNode[0];
            _nowKeys.Clear();
            int slot = 0;
            foreach (var n in map)
            {
                _nowKeys.Add(n.IP);
                if (slot >= _pool.Count) break;
                var r = _pool[slot++];
                if (!r.Go.activeSelf) r.Go.SetActive(true);
                r.FadingOut = false;

                bool newcomer = !_seen.ContainsKey(n.IP);
                if (newcomer)
                {
                    r.Flash = 1f;
                    r.FlashColor = OnlineColor;
                    _seen[n.IP] = n.NodeId;
                }

                Color baseCol;
                if (n.Status != "ONLINE")          baseCol = LostColor;
                else if (n.LatencyMs > 200f)       baseCol = LostColor;
                else if (n.LatencyMs > 50f)        baseCol = DegradedColor;
                else                                baseCol = OnlineColor;
                if (r.Flash > 0f)
                {
                    r.Flash = Mathf.MoveTowards(r.Flash, 0f, Time.deltaTime / 0.4f);
                    baseCol = Color.Lerp(baseCol, r.FlashColor, r.Flash);
                }
                if (r.Dot) r.Dot.color = baseCol;

                if (r.IpCache != n.IP && r.Label)
                {
                    r.IpCache = n.IP;
                    r.Label.text = $"{n.Role,-20} {n.LatencyMs:F0}ms";
                }
                else if (r.Label)
                {
                    // refresh latency cheaply when same row
                    r.Label.text = $"{n.Role,-20} {n.LatencyMs:F0}ms";
                }
                var rt = r.Go.GetComponent<RectTransform>();
                if (rt) rt.anchoredPosition = new Vector2(0f, -(slot - 1) * 32f);
            }
            // mark vanished (reuse scratch list to avoid per-frame alloc)
            _vanishedScratch.Clear();
            foreach (var kv in _seen) if (!_nowKeys.Contains(kv.Key)) _vanishedScratch.Add(kv.Key);
            for (int i = 0; i < _vanishedScratch.Count; i++) _seen.Remove(_vanishedScratch[i]);

            // deactivate unused slots
            for (int i = slot; i < _pool.Count; i++)
            {
                var r = _pool[i];
                if (r.Go.activeSelf)
                {
                    if (!r.FadingOut)
                    {
                        r.FadingOut = true;
                        r.FadeT = 1f;
                        r.FlashColor = LostColor;
                        if (r.Dot) r.Dot.color = LostColor;
                    }
                    r.FadeT = Mathf.MoveTowards(r.FadeT, 0f, Time.deltaTime / 1.0f);
                    if (r.Dot) r.Dot.color = new Color(LostColor.r, LostColor.g, LostColor.b, r.FadeT);
                    if (r.Label) r.Label.color = new Color(1,1,1, r.FadeT);
                    if (r.FadeT <= 0f)
                    {
                        r.Go.SetActive(false);
                        r.IpCache = null;
                    }
                }
            }
        }
    }
}
