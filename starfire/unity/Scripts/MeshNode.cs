using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Starfire
{
    /// <summary>Maintains the WebSocket to ws://localhost:8765 (ADB reverse
    /// tunnel) and pushes incoming packets into DataStore on the main thread.</summary>
    public class MeshNode : MonoBehaviour
    {
        public string Uri = "ws://localhost:8765";
        public float HeartbeatHz = 1f;

        WebSocket _ws;
        readonly ConcurrentQueue<string> _textInbox = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<byte[]> _binInbox  = new ConcurrentQueue<byte[]>();
        float _backoff = 3f;
        bool _running;
        float _nextHeartbeat;
        string _lastPayload;
        TaskCompletionSource<bool> _closedTcs;

        void Start()
        {
            DataStore.ConnectionStatus = "CONNECTING";
            _running = true;
            _ = ConnectLoop();
        }

        async Task ConnectLoop()
        {
            while (_running)
            {
                bool connected = false;
                try
                {
                    DataStore.ConnectionStatus = "CONNECTING";
                    _closedTcs = new TaskCompletionSource<bool>();
                    _ws = new WebSocket(Uri);
                    _ws.OnOpen    += OnOpen;
                    _ws.OnError   += OnError;
                    _ws.OnClose   += OnClose;
                    _ws.OnMessage += OnMessage;
                    // Connect() returns when the socket closes — await it so we
                    // don't spin a second connection while the first is still alive.
                    await _ws.Connect();
                    connected = true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"MeshNode: connect threw {e.Message}");
                }
                // If Connect() returned early without OnClose firing (e.g. transport
                // races on some platforms), wait for the close signal as a safety net.
                if (connected && _closedTcs != null && !_closedTcs.Task.IsCompleted)
                {
                    var winner = await Task.WhenAny(_closedTcs.Task, Task.Delay(50));
                    // brief grace; if still open we proceed anyway — the inner
                    // OnClose handler keeps DataStore consistent.
                }
                if (!_running) break;
                DataStore.ConnectionStatus = "RECONNECTING";
                await Task.Delay((int)(_backoff * 1000));
                _backoff = Mathf.Min(_backoff * 1.5f, 10f);
            }
        }

        void OnOpen()
        {
            Debug.Log("MeshNode: WS open");
            DataStore.ConnectionStatus = "ESTABLISHED";
            _backoff = 3f;
        }

        void OnError(string err)  { Debug.LogWarning($"MeshNode: {err}"); }

        void OnClose(WebSocketCloseCode code)
        {
            Debug.LogWarning($"MeshNode: WS closed {code}");
            DataStore.ConnectionStatus = "LOST";
            DataStore.CurrentMode = "OFFLINE";
            try { _closedTcs?.TrySetResult(true); } catch { }
        }

        void OnMessage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            // PNG (depth map) starts with 0x89 'P' 'N' 'G'
            if (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50)
            {
                _binInbox.Enqueue(bytes);
                return;
            }
            try { _textInbox.Enqueue(Encoding.UTF8.GetString(bytes)); }
            catch { /* not text */ }
        }

        void Update()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            _ws?.DispatchMessageQueue();
#endif
            while (_textInbox.TryDequeue(out var payload)) DispatchText(payload);
            while (_binInbox.TryDequeue(out var blob))    DispatchBinary(blob);

            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                _nextHeartbeat -= Time.deltaTime;
                if (_nextHeartbeat <= 0f)
                {
                    _nextHeartbeat = 1f / Mathf.Max(0.1f, HeartbeatHz);
                    SendHeartbeat();
                }
            }
        }

        async void SendHeartbeat()
        {
            string pkt = "{\"node_id\":\"HUD_PRIME\",\"node_type\":\"quest\",\"mode\":\""
                       + DataStore.CurrentMode + "\",\"status\":\"ACTIVE\",\"timestamp\":"
                       + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0).ToString("F3")
                       + ",\"data\":{}}";
            try { await _ws.SendText(pkt); } catch (Exception) { }
        }

        void DispatchText(string text)
        {
            if (text == _lastPayload) return;
            _lastPayload = text;
            JObject root;
            try { root = JObject.Parse(text); } catch { return; }

            string mode   = (string)root["mode"] ?? DataStore.CurrentMode;
            string status = (string)root["status"] ?? DataStore.ConnectionStatus;
            DataStore.CurrentMode = mode;
            if (!string.IsNullOrEmpty(status)) DataStore.ConnectionStatus = status;

            double ts = (double?)root["timestamp"] ?? 0;
            if (ts > 0)
            {
                double nowSec = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                DataStore.WebSocketLatencyMs = Mathf.Max(0f, (float)((nowSec - ts) * 1000.0));
            }
            DataStore.LastUpdateTime = Time.time;

            JObject data = root["data"] as JObject;
            if (data == null) return;

            // ── detections
            var detJ = data["detections"] as JArray;
            if (detJ != null)
            {
                var list = new List<Detection>(detJ.Count);
                foreach (var d in detJ)
                {
                    var bb = d["bbox"] as JArray;
                    Rect r = new Rect();
                    if (bb != null && bb.Count >= 4)
                    {
                        float x1 = (float)bb[0], y1 = (float)bb[1];
                        float x2 = (float)bb[2], y2 = (float)bb[3];
                        r = new Rect(x1, y1, x2 - x1, y2 - y1);
                    }
                    list.Add(new Detection
                    {
                        ClassName  = (string)d["class"] ?? "?",
                        Confidence = (float?)d["confidence"] ?? 0f,
                        BBox       = r,
                    });
                }
                DataStore.Detections = list;
            }

            // ── depth
            var depth = data["depth"] as JObject;
            if (depth != null)
                DataStore.ClosestObjectDistance = (float?)depth["closest"] ?? 0f;

            // ── faces
            var fac = data["faces"] as JArray;
            if (fac != null)
            {
                var arr = new FaceData[fac.Count];
                for (int i = 0; i < fac.Count; i++)
                {
                    arr[i] = new FaceData
                    {
                        MouthOpen  = (bool?)fac[i]["mouth_open"]  ?? false,
                        BrowRaised = (bool?)fac[i]["brow_raised"] ?? false,
                        EyeSquint  = (bool?)fac[i]["eye_squint"]  ?? false,
                        Position   = (string)fac[i]["position"]   ?? "center",
                    };
                }
                DataStore.Faces = arr;
            }

            // ── gps + accel (relayed from phone)
            var gps = data["gps"] as JObject;
            if (gps != null)
            {
                DataStore.GPS = new GPSData
                {
                    Lat       = (float?)gps["lat"] ?? 0f,
                    Lon       = (float?)gps["lon"] ?? 0f,
                    Altitude  = (float?)gps["altitude"] ?? 0f,
                    Bearing   = (float?)gps["bearing"] ?? 0f,
                    Accuracy  = (float?)gps["accuracy"] ?? 0f,
                    Available = (bool?) gps["available"] ?? false,
                };
                DataStore.Heading = DataStore.GPS.Bearing;
            }

            // ── pipeline_fps
            var fps = data["pipeline_fps"] as JObject;
            if (fps != null)
            {
                DataStore.FPS = new PipelineFPS
                {
                    Yolo   = (float?)fps["yolo"]   ?? 0f,
                    Depth  = (float?)fps["depth"]  ?? 0f,
                    Face   = (float?)fps["face"]   ?? 0f,
                    Camera = (float?)fps["camera"] ?? 0f,
                };
            }

            // ── network_map
            var nm = data["network_map"] as JArray;
            if (nm != null)
            {
                var arr = new NetworkNode[nm.Count];
                for (int i = 0; i < nm.Count; i++)
                {
                    arr[i] = new NetworkNode
                    {
                        IP        = (string)nm[i]["ip"] ?? "?",
                        Role      = (string)nm[i]["role"] ?? "?",
                        NodeId    = (string)nm[i]["node_id"] ?? "?",
                        NodeType  = (string)nm[i]["node_type"] ?? "?",
                        Status    = (string)nm[i]["status"] ?? "?",
                        LatencyMs = (float?)nm[i]["latency_ms"] ?? 0f,
                    };
                }
                DataStore.NetworkMap = arr;
            }
        }

        void DispatchBinary(byte[] data)
        {
            // Depth map PNG — currently we just record arrival; renderers can pick it up
            // through a future texture hook.  Keeping this here so the binary frame
            // doesn't get treated as malformed text.
        }

        async void OnApplicationQuit()
        {
            _running = false;
            if (_ws != null)
            {
                try { await _ws.Close(); } catch { }
            }
        }
    }
}
