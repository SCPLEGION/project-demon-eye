using System.Collections.Generic;
using UnityEngine;

namespace Starfire
{
    public struct Detection
    {
        public string ClassName;
        public float  Confidence;
        public Rect   BBox;
    }

    public struct GPSData
    {
        public float Lat;
        public float Lon;
        public float Altitude;
        public float Bearing;
        public float Accuracy;
        public bool  Available;
    }

    public struct FaceData
    {
        public bool   MouthOpen;
        public bool   BrowRaised;
        public bool   EyeSquint;
        public string Position;
    }

    public struct PipelineFPS
    {
        public float Yolo;
        public float Depth;
        public float Face;
        public float Camera;
    }

    public struct NetworkNode
    {
        public string IP;
        public string Role;
        public string NodeId;
        public string NodeType;
        public string Status;
        public float  LatencyMs;
    }

    /// <summary>Static, thread-aware data bus shared by MeshNode (writer)
    /// and HUDController/panels (readers).</summary>
    public static class DataStore
    {
        public static string CurrentMode      = "OFFLINE";   // FULL | FALLBACK | OFFLINE
        public static string ConnectionStatus = "CONNECTING"; // CONNECTING | ESTABLISHED | LOST | RECONNECTING
        public static List<Detection> Detections = new List<Detection>();
        public static float ClosestObjectDistance = 0f;
        public static GPSData GPS = new GPSData();
        public static float Heading = 0f;
        public static FaceData[] Faces = new FaceData[0];
        public static PipelineFPS FPS = new PipelineFPS();
        public static NetworkNode[] NetworkMap = new NetworkNode[0];
        public static float LastUpdateTime = 0f;
        public static float WebSocketLatencyMs = 0f;

        // Detection lock target (driven by GestureDetector "two-finger point" or voice "Target")
        public static int  LockedTargetIndex = -1;

        // HUD visibility toggle (wrist raise / "Hide")
        public static bool HUDVisible = true;
    }
}
