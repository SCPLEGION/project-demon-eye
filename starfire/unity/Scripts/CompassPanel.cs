using TMPro;
using UnityEngine;

namespace Starfire
{
    public class CompassPanel : MonoBehaviour
    {
        public TMP_Text CardinalRow;        // "N NE E SE S SW W NW" highlighted
        public TMP_Text HeadingArrow;       // single character ▲
        public RectTransform HeadingArrowRect;
        public TMP_Text Coords;
        public TMP_Text AltText;
        public TMP_Text AccText;

        Color _tint = Color.white;
        float _lastHeading = -999f;
        float _lastLat, _lastLon, _lastAlt, _lastAcc;
        bool  _lastAvail;

        static readonly string[] Card = { "N","NE","E","SE","S","SW","W","NW" };

        public void SetTint(Color c)
        {
            _tint = c;
            if (CardinalRow)   CardinalRow.color   = c;
            if (HeadingArrow)  HeadingArrow.color  = c;
            if (Coords)        Coords.color        = c;
            if (AltText)       AltText.color       = c;
            if (AccText)       AccText.color       = c;
        }

        void Update()
        {
            float h = DataStore.Heading;
            bool changedHead = Mathf.Abs(h - _lastHeading) > 0.5f;
            if (changedHead)
            {
                _lastHeading = h;
                int idx = Mathf.RoundToInt(((h % 360f + 360f) % 360f) / 45f) % 8;
                if (CardinalRow)
                {
                    var sb = new System.Text.StringBuilder(48);
                    for (int i = 0; i < Card.Length; i++)
                    {
                        if (i == idx) sb.Append($"<b><color=#FFFFFF>{Card[i]}</color></b>");
                        else sb.Append($"<alpha=#80>{Card[i]}");
                        if (i < Card.Length - 1) sb.Append("  ");
                    }
                    CardinalRow.text = sb.ToString();
                }
                if (HeadingArrowRect)
                    HeadingArrowRect.localRotation = Quaternion.Euler(0f, 0f, -h);
            }

            var g = DataStore.GPS;
            bool changedGps = g.Available != _lastAvail
                           || Mathf.Abs(g.Lat - _lastLat) > 1e-5f
                           || Mathf.Abs(g.Lon - _lastLon) > 1e-5f
                           || Mathf.Abs(g.Altitude - _lastAlt) > 0.5f
                           || Mathf.Abs(g.Accuracy - _lastAcc) > 0.5f;
            if (!changedGps) return;

            _lastAvail = g.Available;
            _lastLat = g.Lat;
            _lastLon = g.Lon;
            _lastAlt = g.Altitude;
            _lastAcc = g.Accuracy;

            if (!g.Available)
            {
                if (Coords) Coords.text = "GPS: ACQUIRING...";
                if (AltText) AltText.text = "ALT: --";
                if (AccText) AccText.text = "ACC: --";
                return;
            }

            string ns = g.Lat >= 0 ? "N" : "S";
            string ew = g.Lon >= 0 ? "E" : "W";
            if (Coords) Coords.text = $"{Mathf.Abs(g.Lat):F4}°{ns}  {Mathf.Abs(g.Lon):F4}°{ew}";
            if (AltText) AltText.text = $"ALT: {g.Altitude:F0}m";
            if (AccText) AccText.text = $"ACC: ±{g.Accuracy:F0}m";
        }
    }
}
