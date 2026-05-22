using UnityEngine;

namespace Starfire
{
    /// <summary>OVRHand/OVRSkeleton-based gesture recognizer.  All toggle outputs
    /// go through InputBus — never reaches OVRInput.RawButton.</summary>
    public class GestureDetector : MonoBehaviour
    {
        public OVRHand LeftHand;
        public OVRHand RightHand;
        public OVRSkeleton LeftSkeleton;
        public OVRSkeleton RightSkeleton;
        public DetectionPanel Detections;     // for two-finger point target lock

        public float Debounce = 0.3f;
        public float HoldFist = 1.0f;
        public float CurlClosed = 60f;        // degrees

        enum GState { Idle, Detecting, Confirmed, Cooldown }
        class GestureSlot
        {
            public GState State;
            public float Held;
            public float CooldownT;
        }

        readonly GestureSlot _rPinch    = new GestureSlot();
        readonly GestureSlot _lPinch    = new GestureSlot();
        readonly GestureSlot _rFist     = new GestureSlot();
        readonly GestureSlot _lFist     = new GestureSlot();
        readonly GestureSlot _palm      = new GestureSlot();
        readonly GestureSlot _thumbsUp  = new GestureSlot();
        readonly GestureSlot _twoFinger = new GestureSlot();
        readonly GestureSlot _wristRaise= new GestureSlot();

        bool _signalWeakLast;

        void Update()
        {
            bool weakL = LeftHand  && LeftHand.IsTracked  && LeftHand.HandConfidence  != OVRHand.TrackingConfidence.High;
            bool weakR = RightHand && RightHand.IsTracked && RightHand.HandConfidence != OVRHand.TrackingConfidence.High;
            bool signalWeak = weakL || weakR;
            if (signalWeak != _signalWeakLast)
            {
                _signalWeakLast = signalWeak;
                InputBus.Instance?.OnSignalWeak?.Invoke(signalWeak);
            }

            bool rOK = RightHand && RightHand.IsTracked && RightHand.HandConfidence == OVRHand.TrackingConfidence.High;
            bool lOK = LeftHand  && LeftHand.IsTracked  && LeftHand.HandConfidence  == OVRHand.TrackingConfidence.High;

            // PINCH — built into OVRHand
            if (rOK)
            {
                bool p = RightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                if (Tick(_rPinch, p, debounce: Debounce, instant: true))
                {
                    InputBus.Instance?.Fire("PINCH RIGHT", InputBus.Instance.OnPanelCycleNext);
                }
                bool tu = IsThumbsUp(RightHand, RightSkeleton);
                if (Tick(_thumbsUp, tu, debounce: Debounce, instant: true))
                {
                    InputBus.Instance?.Fire("THUMBS UP", InputBus.Instance.OnAcknowledge);
                }
                bool tf = IsTwoFingerPoint(RightHand, RightSkeleton);
                if (Tick(_twoFinger, tf, debounce: Debounce, instant: true))
                {
                    Detections?.LockHighestConfidence();
                    InputBus.Instance?.Fire("TARGET LOCK", InputBus.Instance.OnTargetLock);
                }
                bool rf = IsFist(RightSkeleton);
                if (Tick(_rFist, rf, debounce: Debounce, instant: false, holdSeconds: HoldFist))
                {
                    InputBus.Instance?.Fire("COGITATOR DIAGNOSTICS", InputBus.Instance.OnDiagnosticsToggle);
                }
            }
            if (lOK)
            {
                bool p = LeftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                if (Tick(_lPinch, p, debounce: Debounce, instant: true))
                {
                    InputBus.Instance?.Fire("PINCH LEFT", InputBus.Instance.OnPanelCyclePrev);
                }
                bool lf = IsFist(LeftSkeleton);
                if (Tick(_lFist, lf, debounce: Debounce, instant: false, holdSeconds: HoldFist))
                {
                    InputBus.Instance?.Fire("AUGUR ARRAY", InputBus.Instance.OnAugurToggle);
                }
                bool wrist = IsWristRaised(LeftSkeleton);
                if (Tick(_wristRaise, wrist, debounce: Debounce, instant: true))
                {
                    InputBus.Instance?.Fire("HUD TOGGLE", InputBus.Instance.OnHudVisibilityToggle);
                }
            }

            // OPEN PALM (either hand)
            bool palm = (lOK && IsOpenPalm(LeftHand,  LeftSkeleton))
                     || (rOK && IsOpenPalm(RightHand, RightSkeleton));
            if (Tick(_palm, palm, debounce: Debounce, instant: true))
            {
                Detections?.Clear();
                InputBus.Instance?.Fire("DISMISS", InputBus.Instance.OnDismiss);
            }
        }

        /// <summary>State-machine tick.  Returns true exactly once per gesture
        /// confirm.  instant=true fires on rising edge; instant=false fires only
        /// after holding `holdSeconds`.</summary>
        bool Tick(GestureSlot s, bool active, float debounce, bool instant, float holdSeconds = 0f)
        {
            if (s.State == GState.Cooldown)
            {
                s.CooldownT -= Time.deltaTime;
                if (s.CooldownT <= 0f) s.State = GState.Idle;
                return false;
            }
            if (instant)
            {
                if (active && s.State == GState.Idle)
                {
                    s.State = GState.Cooldown;
                    s.CooldownT = debounce;
                    return true;
                }
                if (!active && s.State == GState.Detecting) s.State = GState.Idle;
                return false;
            }
            // hold-to-confirm
            if (active)
            {
                if (s.State == GState.Idle) { s.State = GState.Detecting; s.Held = 0f; }
                s.Held += Time.deltaTime;
                if (s.Held >= holdSeconds)
                {
                    s.State = GState.Cooldown;
                    s.CooldownT = debounce + 0.5f;
                    return true;
                }
            }
            else
            {
                s.Held = 0f;
                if (s.State == GState.Detecting) s.State = GState.Idle;
            }
            return false;
        }

        // ────────────────────────────────────────────────────────────────
        // Gesture predicates
        // ────────────────────────────────────────────────────────────────

        static float Curl(OVRSkeleton skel, OVRSkeleton.BoneId proximal, OVRSkeleton.BoneId distal, OVRSkeleton.BoneId tip)
        {
            if (skel == null || !skel.IsDataValid) return 0f;
            OVRBone p = null, d = null, t = null;
            foreach (var b in skel.Bones)
            {
                if (b.Id == proximal) p = b;
                else if (b.Id == distal) d = b;
                else if (b.Id == tip) t = b;
            }
            if (p == null || d == null || t == null) return 0f;
            Vector3 v1 = p.Transform.position - d.Transform.position;
            Vector3 v2 = t.Transform.position - d.Transform.position;
            return Vector3.Angle(v1, v2);
        }

        bool IsFist(OVRSkeleton skel)
        {
            if (skel == null || !skel.IsDataValid) return false;
            // All four fingers strongly curled
            float c1 = Curl(skel, OVRSkeleton.BoneId.Hand_Index1,  OVRSkeleton.BoneId.Index_Knuckle,  OVRSkeleton.BoneId.HandIndexTip);
            float c2 = Curl(skel, OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Middle_Knuckle, OVRSkeleton.BoneId.HandMiddleTip);
            float c3 = Curl(skel, OVRSkeleton.BoneId.Hand_Ring1,   OVRSkeleton.BoneId.Ring_Knuckle,   OVRSkeleton.BoneId.HandRingTip);
            float c4 = Curl(skel, OVRSkeleton.BoneId.Hand_Pinky1,  OVRSkeleton.BoneId.Pinky_Knuckle,  OVRSkeleton.BoneId.HandPinkyTip);
            // Lower angle = more curled (vectors fold back).  Threshold inverted.
            return c1 < CurlClosed && c2 < CurlClosed && c3 < CurlClosed && c4 < CurlClosed;
        }

        bool IsOpenPalm(OVRHand hand, OVRSkeleton skel)
        {
            if (hand == null || skel == null || !skel.IsDataValid) return false;
            float c1 = Curl(skel, OVRSkeleton.BoneId.Hand_Index1,  OVRSkeleton.BoneId.Index_Knuckle,  OVRSkeleton.BoneId.HandIndexTip);
            float c2 = Curl(skel, OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Middle_Knuckle, OVRSkeleton.BoneId.HandMiddleTip);
            float c3 = Curl(skel, OVRSkeleton.BoneId.Hand_Ring1,   OVRSkeleton.BoneId.Ring_Knuckle,   OVRSkeleton.BoneId.HandRingTip);
            float c4 = Curl(skel, OVRSkeleton.BoneId.Hand_Pinky1,  OVRSkeleton.BoneId.Pinky_Knuckle,  OVRSkeleton.BoneId.HandPinkyTip);
            // open = angles near 180°
            return c1 > 150f && c2 > 150f && c3 > 150f && c4 > 150f
                && !hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        }

        bool IsThumbsUp(OVRHand hand, OVRSkeleton skel)
        {
            if (skel == null || !skel.IsDataValid) return false;
            float i = Curl(skel, OVRSkeleton.BoneId.Hand_Index1, OVRSkeleton.BoneId.Index_Knuckle, OVRSkeleton.BoneId.HandIndexTip);
            float m = Curl(skel, OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Middle_Knuckle, OVRSkeleton.BoneId.HandMiddleTip);
            float thumb = Curl(skel, OVRSkeleton.BoneId.Hand_Thumb1, OVRSkeleton.BoneId.Thumb_Knuckle, OVRSkeleton.BoneId.HandThumbTip);
            // others curled, thumb extended pointing up
            OVRBone thumbTip = null;
            foreach (var b in skel.Bones) if (b.Id == OVRSkeleton.BoneId.HandThumbTip) { thumbTip = b; break; }
            bool pointingUp = thumbTip != null && thumbTip.Transform.up.y > 0.6f;
            return i < CurlClosed && m < CurlClosed && thumb > 150f && pointingUp;
        }

        bool IsTwoFingerPoint(OVRHand hand, OVRSkeleton skel)
        {
            if (skel == null || !skel.IsDataValid) return false;
            float i = Curl(skel, OVRSkeleton.BoneId.Hand_Index1,  OVRSkeleton.BoneId.Index_Knuckle,  OVRSkeleton.BoneId.HandIndexTip);
            float m = Curl(skel, OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Middle_Knuckle, OVRSkeleton.BoneId.HandMiddleTip);
            float r = Curl(skel, OVRSkeleton.BoneId.Hand_Ring1,   OVRSkeleton.BoneId.Ring_Knuckle,   OVRSkeleton.BoneId.HandRingTip);
            float p = Curl(skel, OVRSkeleton.BoneId.Hand_Pinky1,  OVRSkeleton.BoneId.Pinky_Knuckle,  OVRSkeleton.BoneId.HandPinkyTip);
            return i > 150f && m > 150f && r < CurlClosed && p < CurlClosed;
        }

        bool IsWristRaised(OVRSkeleton skel)
        {
            if (skel == null || !skel.IsDataValid) return false;
            OVRBone wrist = null;
            foreach (var b in skel.Bones)
                if (b.Id == OVRSkeleton.BoneId.Hand_WristRoot) { wrist = b; break; }
            if (wrist == null) return false;
            // raised to roughly eye level — head transform at origin of TrackingSpace usually ~1.6m
            // we compare wrist height to camera height when available
            Camera cam = Camera.main;
            float headY = cam != null ? cam.transform.position.y : 1.6f;
            return wrist.Transform.position.y > headY - 0.20f;
        }
    }
}
