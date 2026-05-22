using TMPro;
using UnityEngine;

namespace Starfire
{
    public class FPSDebugOverlay : MonoBehaviour
    {
        public CanvasGroup Cg;
        public TMP_Text Label;

        bool _visible;
        float _alpha;
        string _lastText;

        void OnEnable()
        {
            if (InputBus.Instance != null)
                InputBus.Instance.OnDiagnosticsToggle.AddListener(Toggle);
            if (Cg) Cg.alpha = 0f;
        }

        void OnDisable()
        {
            if (InputBus.Instance != null)
                InputBus.Instance.OnDiagnosticsToggle.RemoveListener(Toggle);
        }

        public void Toggle() { _visible = !_visible; }

        void Update()
        {
            _alpha = Mathf.MoveTowards(_alpha, _visible ? 1f : 0f, Time.deltaTime / 0.2f);
            if (Cg) Cg.alpha = _alpha;
            if (_alpha <= 0f) return;

            var fps = DataStore.FPS;
            int nodeCount = DataStore.NetworkMap != null ? DataStore.NetworkMap.Length : 0;
            string text =
                $"[YOLO]  {fps.Yolo,5:F1} FPS\n" +
                $"[DEPTH] {fps.Depth,5:F1} FPS\n" +
                $"[FACE]  {fps.Face,5:F1} FPS\n" +
                $"[CAM]   {fps.Camera,5:F1} FPS\n" +
                $"[WS]    latency: {DataStore.WebSocketLatencyMs:F0}ms\n" +
                $"[MODE]  {DataStore.CurrentMode}\n" +
                $"[NODES] {nodeCount} online";
            if (text == _lastText) return;
            _lastText = text;
            if (Label) Label.text = text;
        }
    }
}
