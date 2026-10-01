using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{
    public enum PSXTextHorizontalAlignment
    {
        LeftX,
        CenterX,
        RightX
    }

    public enum PSXTextVerticalAlignment
    {
        TopY,
        CenterY,
        BottomY
    }

    /// <summary>
    /// A text UI element for PSX export.
    /// Rendered via psyqo::Font::chainprintf on PS1 hardware.
    /// Attach to a child of a PSXCanvas GameObject.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    [AddComponentMenu("PSX/UI/PSX UI Text")]
    [Icon("Packages/net.psxsplash.splashedit/Icons/PSXUIText.png")]
    public class PSXUIText : MonoBehaviour
    {
        [Tooltip("Name used to reference this element from Lua (max 24 chars).")]
        [SerializeField] private string elementName = "text";

        [Tooltip("Default text content (max 63 chars). Can be changed at runtime via Lua UI.SetText().")]
        [SerializeField] private string defaultText = "";

        [Tooltip("Text color.")]
        [SerializeField] private Color textColor = Color.white;

        [Tooltip("Whether this element is visible when the scene first loads.")]
        [SerializeField] private bool startVisible = true;

        [Tooltip("Custom font override. If null, uses the canvas default font (or built-in system font).")]
        [SerializeField] private PSXFontAsset fontOverride;

        [Tooltip("Whether text should wrap onto additional lines when it exceeds the element width.")]
        [SerializeField] private bool wrapText = false;

        [Tooltip("Horizontal alignment of wrapped text within the element bounds.")]
        [SerializeField] private PSXTextHorizontalAlignment horizontalAlignment = PSXTextHorizontalAlignment.LeftX;

        [Tooltip("Vertical alignment of the full wrapped text block within the element bounds.")]
        [SerializeField] private PSXTextVerticalAlignment verticalAlignment = PSXTextVerticalAlignment.TopY;

        public struct WrappedTextLayout
        {
            public string[] Lines;
            public float LineHeight;
            public float BlockHeight;
            public float MaxLineWidth;
        }

        /// <summary>Element name for Lua access.</summary>
        public string ElementName => elementName;

        /// <summary>Default text content (truncated to 63 chars on export).</summary>
        public string DefaultText => defaultText;

        /// <summary>Text color (RGB, alpha ignored).</summary>
        public Color TextColor => textColor;

        /// <summary>Initial visibility flag.</summary>
        public bool StartVisible => startVisible;

        /// <summary>
        /// Custom font override. If null, inherits from parent PSXCanvas.DefaultFont.
        /// If that is also null, uses the built-in system font.
        /// </summary>
        public PSXFontAsset FontOverride => fontOverride;

        /// <summary>Whether text should automatically wrap to additional lines.</summary>
        public bool WrapText => wrapText;

        /// <summary>Horizontal alignment of wrapped text within the element bounds.</summary>
        public PSXTextHorizontalAlignment HorizontalAlignment => horizontalAlignment;

        /// <summary>Vertical alignment of the full wrapped text block within the element bounds.</summary>
        public PSXTextVerticalAlignment VerticalAlignment => verticalAlignment;

        /// <summary>
        /// Resolve the effective font for this text element.
        /// Checks: fontOverride -> parent PSXCanvas.DefaultFont -> null (system font).
        /// </summary>
        public PSXFontAsset GetEffectiveFont()
        {
            if (fontOverride != null) return fontOverride;
            PSXCanvas canvas = GetComponentInParent<PSXCanvas>();
            if (canvas != null && canvas.DefaultFont != null) return canvas.DefaultFont;
            return null; // system font
        }

        /// <summary>
        /// Build a wrapped line layout for the current text based on the available render width.
        /// The block height is based on the full wrapped set of lines so vertical alignment works correctly.
        /// </summary>
        public WrappedTextLayout GetWrappedLayout(PSXFontAsset font, int glyphWidth, int glyphHeight, float pixelScale, float containerWidth)
        {
            string text = string.IsNullOrEmpty(defaultText) ? "[empty]" : defaultText;
            float lineHeight = glyphHeight * pixelScale;
            if (containerWidth <= 0.001f)
            {
                return new WrappedTextLayout
                {
                    Lines = [text],
                    LineHeight = lineHeight,
                    BlockHeight = lineHeight,
                    MaxLineWidth = MeasureLineWidth(text, font, glyphWidth, pixelScale)
                };
            }

            var lines = new List<string>();
            var builder = new StringBuilder();
            float currentLineWidth = 0f;
            float maxLineWidth = 0f;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\n')
                {
                    if (builder.Length > 0)
                    {
                        lines.Add(builder.ToString());
                        builder.Clear();
                    }
                    else
                    {
                        lines.Add(string.Empty);
                    }

                    currentLineWidth = 0f;
                    continue;
                }

                if (ch < 32 || ch > 126)
                    continue;

                int charIdx = ch - 32;
                float advance = glyphWidth;
                if (font != null && font.AdvanceWidths != null && charIdx < font.AdvanceWidths.Length)
                    advance = font.AdvanceWidths[charIdx];
                advance *= pixelScale;

                if (wrapText && builder.Length > 0 && currentLineWidth + advance > containerWidth)
                {
                    lines.Add(builder.ToString());
                    builder.Clear();
                    currentLineWidth = 0f;
                }

                builder.Append(ch);
                currentLineWidth += advance;
            }

            if (builder.Length > 0)
            {
                lines.Add(builder.ToString());
            }
            else if (lines.Count == 0)
            {
                lines.Add(string.Empty);
            }

            foreach (string line in lines)
            {
                float lineWidth = MeasureLineWidth(line, font, glyphWidth, pixelScale);
                if (lineWidth > maxLineWidth)
                    maxLineWidth = lineWidth;
            }

            return new WrappedTextLayout
            {
                Lines = [.. lines],
                LineHeight = lineHeight,
                BlockHeight = Mathf.Max(lineHeight, lines.Count * lineHeight),
                MaxLineWidth = maxLineWidth
            };
        }

        public float MeasureLineWidth(string line, PSXFontAsset font, int glyphWidth, float pixelScale)
        {
            float width = 0f;
            foreach (char ch in line)
            {
                if (ch < 32 || ch > 126)
                    continue;

                int charIdx = ch - 32;
                float advance = glyphWidth;
                if (font != null && font.AdvanceWidths != null && charIdx < font.AdvanceWidths.Length)
                    advance = font.AdvanceWidths[charIdx];
                width += advance * pixelScale;
            }

            return width;
        }
    }
}
