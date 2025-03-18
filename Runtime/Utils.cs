using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{
    /// <summary>
    /// Represents a prohibited area in PlayStation 2D VRAM where textures should not be packed.
    /// This class provides conversion methods to and from Unity's Rect structure.
    /// </summary>
    public class ProhibitedArea
    {
        // X and Y coordinates of the prohibited area in VRAM.
        public int X;
        public int Y;
        // Width and height of the prohibited area.
        public int Width;
        public int Height;

        /// <summary>
        /// Creates a ProhibitedArea instance from a Unity Rect.
        /// The floating-point values of the Rect are rounded to the nearest integer.
        /// </summary>
        /// <param name="rect">The Unity Rect representing the prohibited area.</param>
        /// <returns>A new ProhibitedArea with integer dimensions.</returns>
        public static ProhibitedArea FromUnityRect(Rect rect)
        {
            return new ProhibitedArea
            {
                X = Mathf.RoundToInt(rect.x),
                Y = Mathf.RoundToInt(rect.y),
                Width = Mathf.RoundToInt(rect.width),
                Height = Mathf.RoundToInt(rect.height)
            };
        }

        /// <summary>
        /// Converts the ProhibitedArea back into a Unity Rect.
        /// </summary>
        /// <returns>A Unity Rect with the same area as defined by this ProhibitedArea.</returns>
        public Rect ToUnityRect()
        {
            return new Rect(X, Y, Width, Height);
        }
    }


    public static class Utils
    {
        private static string _psxDataPath = "Assets/PSXData.asset";

        public static (Rect, Rect) BufferForResolution(Vector2 selectedResolution, bool verticalLayout, Vector2 offset = default)
        {
            if (offset == default)
            {
                offset = Vector2.zero;
            }
            Rect buffer1 = new Rect(offset.x, offset.y, selectedResolution.x, selectedResolution.y);
            Rect buffer2 = verticalLayout ? new Rect(offset.x, 256, selectedResolution.x, selectedResolution.y)
                                          : new Rect(offset.x + selectedResolution.x, offset.y, selectedResolution.x, selectedResolution.y);
            return (buffer1, buffer2);
        }

        /// <summary>
        /// Loads stored PSX data from the asset.
        /// </summary>
        public static PSXData LoadData(out Vector2 selectedResolution, out bool dualBuffering, out bool verticalLayout, out List<ProhibitedArea> prohibitedAreas)
        {
            var _psxData = AssetDatabase.LoadAssetAtPath<PSXData>(_psxDataPath);
            if (!_psxData)
            {
                _psxData = ScriptableObject.CreateInstance<PSXData>();
                AssetDatabase.CreateAsset(_psxData, _psxDataPath);
                AssetDatabase.SaveAssets();
            }

            selectedResolution = _psxData.OutputResolution;
            dualBuffering = _psxData.DualBuffering;
            verticalLayout = _psxData.VerticalBuffering;
            prohibitedAreas = _psxData.ProhibitedAreas;
            return _psxData;
        }
    }
}
