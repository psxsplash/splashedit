#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SplashEdit.RuntimeCode;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// All IMGUI draw calls for the PSX Timeline Editor.
    /// Stateless - reads from PSXTimelineState, draws to screen.
    /// </summary>
    public static class PSXTimelineDrawer
    {
        // Keyframe diamond size (half-extent)
        private const float KF_SIZE = 5f;
        // Playhead triangle size
        private const float PH_TRI_SIZE = 8f;

        // Track type category colors (tinted backgrounds)
        private static readonly Color CameraTrackTint = new Color(0.3f, 0.85f, 0.95f, 0.08f);
        private static readonly Color ObjectTrackTint = new Color(0.35f, 0.85f, 0.45f, 0.08f);
        private static readonly Color UITrackTint = new Color(0.85f, 0.3f, 0.65f, 0.08f);
        private static readonly Color VibrationTrackTint = new Color(0.95f, 0.75f, 0.2f, 0.08f);

        // =====================================================================
        // Toolbar
        // =====================================================================

        public static void DrawToolbar(PSXTimelineState state)
        {
            var rect = state.ToolbarRect;
            EditorGUI.DrawRect(rect, PSXEditorStyles.BackgroundMedium);

            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Transport buttons (set RequestedAction for the window to handle)
            if (state.IsPlaying)
            {
                if (GUILayout.Button("||", EditorStyles.toolbarButton, GUILayout.Width(28)))
                    state.RequestedAction = PSXTimelineState.ToolbarAction.Pause;
            }
            else
            {
                if (GUILayout.Button("\u25B6", EditorStyles.toolbarButton, GUILayout.Width(28)))
                    state.RequestedAction = PSXTimelineState.ToolbarAction.Play;
            }

            if (GUILayout.Button("\u25A0", EditorStyles.toolbarButton, GUILayout.Width(28)))
                state.RequestedAction = PSXTimelineState.ToolbarAction.Stop;

            GUILayout.Space(8);

            // Frame display
            EditorGUILayout.LabelField("Frame:", GUILayout.Width(42));
            int frame = Mathf.RoundToInt(state.PlayheadFrame);
            int newFrame = EditorGUILayout.IntField(frame, GUILayout.Width(50));
            if (newFrame != frame)
                state.PlayheadFrame = Mathf.Clamp(newFrame, 0, state.DurationFrames);

            float sec = state.PlayheadFrame / 30f;
            float durSec = state.DurationFrames / 30f;
            EditorGUILayout.LabelField($"{sec:F2}s / {durSec:F2}s", PSXEditorStyles.CenteredLabel, GUILayout.Width(100));

            GUILayout.FlexibleSpace();

            // Zoom slider
            EditorGUILayout.LabelField("Zoom:", GUILayout.Width(38));
            state.PixelsPerFrame = GUILayout.HorizontalSlider(state.PixelsPerFrame, 1f, 20f, GUILayout.Width(80));

            GUILayout.Space(4);

            // End Preview button + indicator
            if (state.IsPreviewing)
            {
                var oldColor = GUI.color;
                GUI.color = new Color(1f, 0.4f, 0.4f);
                if (GUILayout.Button("End Preview", EditorStyles.toolbarButton, GUILayout.Width(80)))
                    state.RequestedAction = PSXTimelineState.ToolbarAction.EndPreview;
                GUI.color = oldColor;
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // =====================================================================
        // Time Ruler
        // =====================================================================

        public static void DrawTimeRuler(PSXTimelineState state)
        {
            var rect = state.TimeRulerRect;
            EditorGUI.DrawRect(rect, PSXEditorStyles.BackgroundDark);

            // Bottom border
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), PSXEditorStyles.TextMuted);

            var (startFrame, endFrame) = state.GetVisibleFrameRange();
            endFrame = Mathf.Min(endFrame, state.DurationFrames + 30);

            // Determine tick spacing based on zoom
            int majorInterval = 30; // 1 second
            int minorInterval = 5;
            if (state.PixelsPerFrame < 2f) { majorInterval = 150; minorInterval = 30; }
            else if (state.PixelsPerFrame < 4f) { majorInterval = 60; minorInterval = 15; }

            // Draw ticks
            for (int f = (startFrame / minorInterval) * minorInterval; f <= endFrame; f += minorInterval)
            {
                if (f < 0) continue;
                float x = state.FrameToPixelX(f);
                if (x < 0 || x > rect.width) continue;

                float absX = rect.x + x;
                bool isMajor = (f % majorInterval) == 0;

                if (isMajor)
                {
                    // Major tick + label
                    EditorGUI.DrawRect(new Rect(absX, rect.y + 2, 1, rect.height - 4), PSXEditorStyles.TextSecondary);
                    float sec2 = f / 30f;
                    string label = sec2 == (int)sec2 ? $"{(int)sec2}s" : $"{sec2:F1}s";
                    GUI.Label(new Rect(absX + 3, rect.y + 1, 40, 16), label,
                        GetRulerLabelStyle());
                }
                else
                {
                    // Minor tick
                    float tickH = rect.height * 0.35f;
                    EditorGUI.DrawRect(new Rect(absX, rect.yMax - tickH - 1, 1, tickH), PSXEditorStyles.TextMuted);
                }
            }

            // Duration end marker
            float endX = rect.x + state.FrameToPixelX(state.DurationFrames);
            if (endX >= rect.x && endX <= rect.xMax)
            {
                EditorGUI.DrawRect(new Rect(endX, rect.y, 2, rect.height), PSXEditorStyles.Error * 0.7f);
            }

            // Playhead triangle on ruler
            float phX = rect.x + state.FrameToPixelX(state.PlayheadFrame);
            if (phX >= rect.x && phX <= rect.xMax)
                DrawPlayheadTriangle(phX, rect.yMax - 2);
        }

        // =====================================================================
        // Track Headers
        // =====================================================================

        public static void DrawTrackHeaders(PSXTimelineState state)
        {
            var rect = state.TrackHeaderRect;
            EditorGUI.DrawRect(rect, PSXEditorStyles.BackgroundMedium);

            // Right border
            EditorGUI.DrawRect(new Rect(rect.xMax - 1, rect.y, 1, rect.height), PSXEditorStyles.TextMuted);

            var tracks = state.Tracks;
            if (tracks == null) return;

            float y = rect.y - state.ScrollY;

            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                var laneRect = new Rect(rect.x, y, rect.width - 1, PSXTimelineState.TrackHeight);

                if (laneRect.yMax > rect.y && laneRect.y < rect.yMax)
                {
                    // Selection highlight
                    if (state.SelectedTrackIndex == i)
                        EditorGUI.DrawRect(laneRect, PSXEditorStyles.BackgroundHighlight * 0.5f);

                    // Alternating background
                    if (i % 2 == 1)
                        EditorGUI.DrawRect(laneRect, new Color(0, 0, 0, 0.1f));

                    // Track label
                    string label = GetTrackLabel(track);
                    Color labelColor = GetTrackCategoryColor(track);
                    var labelStyle = GetTrackHeaderStyle();
                    labelStyle.normal.textColor = labelColor;
                    GUI.Label(new Rect(rect.x + 6, y + 4, rect.width - 30, 20), label, labelStyle);

                    // Bottom border
                    EditorGUI.DrawRect(new Rect(rect.x, y + PSXTimelineState.TrackHeight - 1, rect.width, 1),
                        new Color(0, 0, 0, 0.2f));
                }

                y += PSXTimelineState.TrackHeight;
            }

            // Event lane headers
            if (state.IsCutscene)
            {
                DrawEventLaneHeader(rect, ref y, "Audio Events", PSXEditorStyles.AccentMagenta, state);
            }
            if (state.SkinAnimEvents != null)
            {
                DrawEventLaneHeader(rect, ref y, "Skin Anims", PSXEditorStyles.AccentGreen, state);
            }

            // "Add Track" row
            var addRect = new Rect(rect.x, y, rect.width - 1, PSXTimelineState.TrackHeight);
            if (addRect.yMax > rect.y && addRect.y < rect.yMax)
            {
                var addStyle = GetTrackHeaderStyle();
                addStyle.normal.textColor = PSXEditorStyles.TextMuted;
                GUI.Label(new Rect(rect.x + 6, y + 4, rect.width - 12, 20), "+ Add Track", addStyle);
            }
        }

        private static void DrawEventLaneHeader(Rect rect, ref float y, string label, Color color, PSXTimelineState state)
        {
            var laneRect = new Rect(rect.x, y, rect.width - 1, PSXTimelineState.EventLaneHeight);
            if (laneRect.yMax > rect.y && laneRect.y < rect.yMax)
            {
                EditorGUI.DrawRect(laneRect, new Color(color.r, color.g, color.b, 0.06f));
                var style = GetTrackHeaderStyle();
                style.normal.textColor = color;
                style.fontStyle = FontStyle.Italic;
                GUI.Label(new Rect(rect.x + 6, y + 3, rect.width - 12, 18), label, style);
                style.fontStyle = FontStyle.Normal;
                EditorGUI.DrawRect(new Rect(rect.x, y + PSXTimelineState.EventLaneHeight - 1, rect.width, 1),
                    new Color(0, 0, 0, 0.2f));
            }
            y += PSXTimelineState.EventLaneHeight;
        }

        // =====================================================================
        // Track Lanes (keyframes, interpolation visualization)
        // =====================================================================

        public static void DrawTrackLanes(PSXTimelineState state)
        {
            var rect = state.TimelineAreaRect;
            EditorGUI.DrawRect(rect, PSXEditorStyles.BackgroundDark);

            var tracks = state.Tracks;
            if (tracks == null) return;

            // Clip to timeline area
            GUI.BeginClip(rect);
            var localRect = new Rect(0, 0, rect.width, rect.height);

            float y = -state.ScrollY;

            // Grid lines (vertical, at major tick marks)
            DrawGridLines(state, localRect);

            // Duration end line
            float endX = state.FrameToPixelX(state.DurationFrames);
            if (endX >= 0 && endX <= localRect.width)
                EditorGUI.DrawRect(new Rect(endX, 0, 1, localRect.height), PSXEditorStyles.Error * 0.3f);

            // Track lanes
            for (int ti = 0; ti < tracks.Count; ti++)
            {
                var track = tracks[ti];
                var laneY = y;

                // Alternating + category tint
                if (ti % 2 == 1)
                    EditorGUI.DrawRect(new Rect(0, laneY, localRect.width, PSXTimelineState.TrackHeight),
                        new Color(1, 1, 1, 0.02f));

                Color tint = GetTrackTint(track);
                if (tint.a > 0)
                    EditorGUI.DrawRect(new Rect(0, laneY, localRect.width, PSXTimelineState.TrackHeight), tint);

                // Lane bottom border
                EditorGUI.DrawRect(new Rect(0, laneY + PSXTimelineState.TrackHeight - 1, localRect.width, 1),
                    new Color(0, 0, 0, 0.15f));

                // Draw keyframes and connections
                if (track.Keyframes != null && track.Keyframes.Count > 0)
                {
                    float centerY = laneY + PSXTimelineState.TrackHeight * 0.5f;
                    bool isBool = track.TrackType == PSXTrackType.ObjectActive ||
                                  track.TrackType == PSXTrackType.UICanvasVisible ||
                                  track.TrackType == PSXTrackType.UIElementVisible;

                    if (isBool)
                        DrawBoolTrack(state, track, ti, laneY, centerY, localRect);
                    else
                        DrawValueTrack(state, track, ti, centerY, localRect);
                }

                y += PSXTimelineState.TrackHeight;
            }

            // Event lanes
            if (state.IsCutscene && state.AudioEvents != null)
            {
                DrawAudioEventLane(state, y, localRect);
                y += PSXTimelineState.EventLaneHeight;
            }
            if (state.SkinAnimEvents != null)
            {
                DrawSkinAnimEventLane(state, y, localRect);
                y += PSXTimelineState.EventLaneHeight;
            }

            // Playhead line
            DrawPlayheadLine(state, localRect);

            GUI.EndClip();
        }

        // ── Grid lines ──

        private static void DrawGridLines(PSXTimelineState state, Rect localRect)
        {
            var (startFrame, endFrame) = state.GetVisibleFrameRange();
            int majorInterval = 30;
            if (state.PixelsPerFrame < 2f) majorInterval = 150;
            else if (state.PixelsPerFrame < 4f) majorInterval = 60;

            Color gridColor = new Color(1, 1, 1, 0.04f);
            for (int f = (startFrame / majorInterval) * majorInterval; f <= endFrame; f += majorInterval)
            {
                if (f < 0) continue;
                float x = state.FrameToPixelX(f);
                if (x >= 0 && x <= localRect.width)
                    EditorGUI.DrawRect(new Rect(x, 0, 1, localRect.height), gridColor);
            }
        }

        // ── Value tracks (position, rotation, etc.) ──

        private static void DrawValueTrack(PSXTimelineState state, PSXCutsceneTrack track, int trackIdx,
            float centerY, Rect localRect)
        {
            var kfs = track.Keyframes;

            // Draw connection lines between keyframes
            for (int i = 0; i < kfs.Count - 1; i++)
            {
                float x1 = state.FrameToPixelX(kfs[i].Frame);
                float x2 = state.FrameToPixelX(kfs[i + 1].Frame);
                if (x2 < 0 || x1 > localRect.width) continue;

                Color lineColor = new Color(1, 1, 1, 0.15f);
                if (kfs[i].Interp == PSXInterpMode.Step)
                {
                    // Step: horizontal line, then vertical drop
                    EditorGUI.DrawRect(new Rect(x1, centerY, x2 - x1, 1), lineColor);
                }
                else
                {
                    // Linear / ease: simple line (horizontal for simplicity in IMGUI)
                    EditorGUI.DrawRect(new Rect(x1, centerY, x2 - x1, 1), lineColor);
                }
            }

            // Draw keyframe diamonds
            for (int ki = 0; ki < kfs.Count; ki++)
            {
                float x = state.FrameToPixelX(kfs[ki].Frame);
                if (x < -KF_SIZE || x > localRect.width + KF_SIZE) continue;

                bool selected = (state.SelectedTrackIndex == trackIdx && state.SelectedKeyframeIndex == ki)
                    || state.MultiSelection.Contains((trackIdx, ki));
                DrawKeyframeDiamond(x, centerY, selected);
            }
        }

        // ── Bool tracks (active/visible) ──

        private static void DrawBoolTrack(PSXTimelineState state, PSXCutsceneTrack track, int trackIdx,
            float laneY, float centerY, Rect localRect)
        {
            var kfs = track.Keyframes;
            float barTop = laneY + 6;
            float barH = PSXTimelineState.TrackHeight - 12;
            Color onColor = new Color(PSXEditorStyles.AccentGreen.r, PSXEditorStyles.AccentGreen.g,
                PSXEditorStyles.AccentGreen.b, 0.25f);
            Color offColor = new Color(0, 0, 0, 0.1f);

            // Draw state regions
            for (int i = 0; i < kfs.Count; i++)
            {
                float x1 = state.FrameToPixelX(kfs[i].Frame);
                float x2 = (i < kfs.Count - 1) ? state.FrameToPixelX(kfs[i + 1].Frame)
                    : state.FrameToPixelX(state.DurationFrames);
                if (x2 < 0 || x1 > localRect.width) continue;

                x1 = Mathf.Max(x1, 0);
                x2 = Mathf.Min(x2, localRect.width);

                bool isOn = kfs[i].Value.x > 0.5f;
                EditorGUI.DrawRect(new Rect(x1, barTop, x2 - x1, barH), isOn ? onColor : offColor);
            }

            // Draw keyframe diamonds on top
            for (int ki = 0; ki < kfs.Count; ki++)
            {
                float x = state.FrameToPixelX(kfs[ki].Frame);
                if (x < -KF_SIZE || x > localRect.width + KF_SIZE) continue;

                bool selected = (state.SelectedTrackIndex == trackIdx && state.SelectedKeyframeIndex == ki)
                    || state.MultiSelection.Contains((trackIdx, ki));
                DrawKeyframeDiamond(x, centerY, selected);
            }
        }

        // ── Audio event lane ──

        private static void DrawAudioEventLane(PSXTimelineState state, float y, Rect localRect)
        {
            var events = state.AudioEvents;
            if (events == null) return;

            EditorGUI.DrawRect(new Rect(0, y, localRect.width, PSXTimelineState.EventLaneHeight),
                new Color(PSXEditorStyles.AccentMagenta.r, PSXEditorStyles.AccentMagenta.g,
                    PSXEditorStyles.AccentMagenta.b, 0.04f));
            EditorGUI.DrawRect(new Rect(0, y + PSXTimelineState.EventLaneHeight - 1, localRect.width, 1),
                new Color(0, 0, 0, 0.15f));

            for (int i = 0; i < events.Count; i++)
            {
                float x = state.FrameToPixelX(events[i].Frame);
                if (x < -60 || x > localRect.width) continue;

                bool selected = state.SelectedEventType == 0 && state.SelectedEventIndex == i;
                Color bg = selected ? PSXEditorStyles.AccentMagenta * 0.6f : PSXEditorStyles.AccentMagenta * 0.3f;
                float w = Mathf.Max(60, events[i].ClipName.Length * 6f);
                var evtRect = new Rect(x, y + 3, w, PSXTimelineState.EventLaneHeight - 6);
                EditorGUI.DrawRect(evtRect, bg);
                DrawBorder(evtRect, PSXEditorStyles.AccentMagenta * 0.7f);

                var labelStyle = GetEventLabelStyle();
                GUI.Label(new Rect(x + 3, y + 4, w - 6, 16), events[i].ClipName, labelStyle);
            }
        }

        // ── Skin anim event lane ──

        private static void DrawSkinAnimEventLane(PSXTimelineState state, float y, Rect localRect)
        {
            var events = state.SkinAnimEvents;
            if (events == null) return;

            EditorGUI.DrawRect(new Rect(0, y, localRect.width, PSXTimelineState.EventLaneHeight),
                new Color(PSXEditorStyles.AccentGreen.r, PSXEditorStyles.AccentGreen.g,
                    PSXEditorStyles.AccentGreen.b, 0.04f));
            EditorGUI.DrawRect(new Rect(0, y + PSXTimelineState.EventLaneHeight - 1, localRect.width, 1),
                new Color(0, 0, 0, 0.15f));

            for (int i = 0; i < events.Count; i++)
            {
                float x = state.FrameToPixelX(events[i].Frame);
                if (x < -80 || x > localRect.width) continue;

                bool selected = state.SelectedEventType == 1 && state.SelectedEventIndex == i;
                Color bg = selected ? PSXEditorStyles.AccentGreen * 0.5f : PSXEditorStyles.AccentGreen * 0.25f;
                string label = $"{events[i].TargetObjectName}:{events[i].ClipName}";
                float w = Mathf.Max(80, label.Length * 6f);
                var evtRect = new Rect(x, y + 3, w, PSXTimelineState.EventLaneHeight - 6);
                EditorGUI.DrawRect(evtRect, bg);
                DrawBorder(evtRect, PSXEditorStyles.AccentGreen * 0.6f);

                var labelStyle = GetEventLabelStyle();
                GUI.Label(new Rect(x + 3, y + 4, w - 6, 16), label, labelStyle);
            }
        }

        // =====================================================================
        // Playhead
        // =====================================================================

        private static void DrawPlayheadLine(PSXTimelineState state, Rect localRect)
        {
            float x = state.FrameToPixelX(state.PlayheadFrame);
            if (x < 0 || x > localRect.width) return;

            EditorGUI.DrawRect(new Rect(x, 0, 2, localRect.height), PSXEditorStyles.AccentGold);
        }

        private static void DrawPlayheadTriangle(float x, float bottomY)
        {
            // Draw a simple downward-pointing triangle using rects (IMGUI approximation)
            Color c = PSXEditorStyles.AccentGold;
            for (int row = 0; row < (int)PH_TRI_SIZE; row++)
            {
                float halfW = PH_TRI_SIZE - row;
                EditorGUI.DrawRect(new Rect(x - halfW, bottomY - PH_TRI_SIZE + row, halfW * 2, 1), c);
            }
        }

        // =====================================================================
        // Keyframe Diamond
        // =====================================================================

        private static void DrawKeyframeDiamond(float cx, float cy, bool selected)
        {
            Color fill = selected ? PSXEditorStyles.AccentCyan : PSXEditorStyles.AccentGold;
            Color border = selected ? new Color(0.5f, 0.95f, 1f) : new Color(0.8f, 0.6f, 0.15f);

            // Diamond as rotated square (drawn with horizontal rects, row by row)
            int size = (int)KF_SIZE;
            for (int row = -size; row <= size; row++)
            {
                int halfW = size - Mathf.Abs(row);
                if (halfW <= 0) continue;
                EditorGUI.DrawRect(new Rect(cx - halfW, cy + row, halfW * 2, 1), fill);
            }
            // Border pixels (top, bottom, left, right points)
            EditorGUI.DrawRect(new Rect(cx - 1, cy - size, 2, 1), border);
            EditorGUI.DrawRect(new Rect(cx - 1, cy + size, 2, 1), border);
            EditorGUI.DrawRect(new Rect(cx - size, cy - 1, 1, 2), border);
            EditorGUI.DrawRect(new Rect(cx + size, cy - 1, 1, 2), border);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        public static string GetTrackLabel(PSXCutsceneTrack track)
        {
            switch (track.TrackType)
            {
                case PSXTrackType.CameraPosition: return "Cam Position";
                case PSXTrackType.CameraRotation: return "Cam Rotation";
                case PSXTrackType.CameraH: return "Cam FOV (H)";
                case PSXTrackType.ObjectPosition: return $"Pos: {track.ObjectName}";
                case PSXTrackType.ObjectRotation: return $"Rot: {track.ObjectName}";
                case PSXTrackType.ObjectActive: return $"Active: {track.ObjectName}";
                case PSXTrackType.UICanvasVisible: return $"Canvas: {track.UICanvasName}";
                case PSXTrackType.UIElementVisible: return $"Vis: {track.UICanvasName}/{track.UIElementName}";
                case PSXTrackType.UIProgress: return $"Prog: {track.UICanvasName}/{track.UIElementName}";
                case PSXTrackType.UIPosition: return $"Pos: {track.UICanvasName}/{track.UIElementName}";
                case PSXTrackType.UIColor: return $"Color: {track.UICanvasName}/{track.UIElementName}";
                case PSXTrackType.RumbleSmall: return "Rumble: Small Motor";
                case PSXTrackType.RumbleLarge: return "Rumble: Large Motor";
                default: return $"Track {track.TrackType}";
            }
        }

        private static Color GetTrackCategoryColor(PSXCutsceneTrack track)
        {
            if (track.IsCameraTrack) return PSXEditorStyles.AccentCyan;
            if (track.IsUITrack) return PSXEditorStyles.AccentMagenta;
            if (track.IsVibrationTrack) return PSXEditorStyles.AccentGold;
            return PSXEditorStyles.AccentGreen;
        }

        private static Color GetTrackTint(PSXCutsceneTrack track)
        {
            if (track.IsCameraTrack) return CameraTrackTint;
            if (track.IsUITrack) return UITrackTint;
            if (track.IsVibrationTrack) return VibrationTrackTint;
            return ObjectTrackTint;
        }

        private static void DrawBorder(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1, rect.y, 1, rect.height), color);
        }

        // ── Cached styles ──

        private static GUIStyle _rulerLabelStyle;
        private static GUIStyle GetRulerLabelStyle()
        {
            if (_rulerLabelStyle == null)
            {
                _rulerLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 9,
                    alignment = TextAnchor.MiddleLeft,
                };
                _rulerLabelStyle.normal.textColor = PSXEditorStyles.TextSecondary;
            }
            return _rulerLabelStyle;
        }

        private static GUIStyle _trackHeaderStyle;
        private static GUIStyle GetTrackHeaderStyle()
        {
            if (_trackHeaderStyle == null)
            {
                _trackHeaderStyle = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Normal,
                    clipping = TextClipping.Clip,
                };
            }
            return _trackHeaderStyle;
        }

        private static GUIStyle _eventLabelStyle;
        private static GUIStyle GetEventLabelStyle()
        {
            if (_eventLabelStyle == null)
            {
                _eventLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 9,
                    alignment = TextAnchor.MiddleLeft,
                    clipping = TextClipping.Clip,
                };
                _eventLabelStyle.normal.textColor = PSXEditorStyles.TextPrimary;
            }
            return _eventLabelStyle;
        }

        /// <summary>
        /// Hit test: returns the (trackIndex, keyframeIndex) of the keyframe at the given
        /// position in timeline-local coordinates, or (-1,-1) if nothing hit.
        /// </summary>
        public static (int track, int kf) HitTestKeyframe(PSXTimelineState state, Vector2 localPos)
        {
            var tracks = state.Tracks;
            if (tracks == null) return (-1, -1);

            float y = -state.ScrollY;
            for (int ti = 0; ti < tracks.Count; ti++)
            {
                float centerY = y + PSXTimelineState.TrackHeight * 0.5f;

                if (tracks[ti].Keyframes != null)
                {
                    for (int ki = 0; ki < tracks[ti].Keyframes.Count; ki++)
                    {
                        float kfX = state.FrameToPixelX(tracks[ti].Keyframes[ki].Frame);
                        if (Mathf.Abs(localPos.x - kfX) <= KF_SIZE + 2 &&
                            Mathf.Abs(localPos.y - centerY) <= KF_SIZE + 2)
                            return (ti, ki);
                    }
                }

                y += PSXTimelineState.TrackHeight;
            }
            return (-1, -1);
        }

        /// <summary>
        /// Hit test for event lanes. Returns (eventType, eventIndex) or (-1, -1).
        /// eventType: 0 = audio, 1 = skin anim.
        /// </summary>
        public static (int type, int index) HitTestEvent(PSXTimelineState state, Vector2 localPos)
        {
            var tracks = state.Tracks;
            float y = -state.ScrollY + (tracks?.Count ?? 0) * PSXTimelineState.TrackHeight;

            // Audio events
            if (state.IsCutscene && state.AudioEvents != null)
            {
                if (localPos.y >= y && localPos.y < y + PSXTimelineState.EventLaneHeight)
                {
                    for (int i = 0; i < state.AudioEvents.Count; i++)
                    {
                        float x = state.FrameToPixelX(state.AudioEvents[i].Frame);
                        float w = Mathf.Max(60, state.AudioEvents[i].ClipName.Length * 6f);
                        if (localPos.x >= x && localPos.x <= x + w)
                            return (0, i);
                    }
                }
                y += PSXTimelineState.EventLaneHeight;
            }

            // Skin anim events
            if (state.SkinAnimEvents != null)
            {
                if (localPos.y >= y && localPos.y < y + PSXTimelineState.EventLaneHeight)
                {
                    for (int i = 0; i < state.SkinAnimEvents.Count; i++)
                    {
                        float x = state.FrameToPixelX(state.SkinAnimEvents[i].Frame);
                        string label = $"{state.SkinAnimEvents[i].TargetObjectName}:{state.SkinAnimEvents[i].ClipName}";
                        float w = Mathf.Max(80, label.Length * 6f);
                        if (localPos.x >= x && localPos.x <= x + w)
                            return (1, i);
                    }
                }
            }

            return (-1, -1);
        }

        /// <summary>
        /// Hit test for track header "Add Track" row. Returns the track index for the lane
        /// clicked, or -1 if the "Add Track" row was clicked, or -2 for nothing.
        /// </summary>
        public static int HitTestTrackHeader(PSXTimelineState state, Vector2 localPos)
        {
            float y = -state.ScrollY;
            var tracks = state.Tracks;
            int trackCount = tracks?.Count ?? 0;

            for (int i = 0; i < trackCount; i++)
            {
                if (localPos.y >= y && localPos.y < y + PSXTimelineState.TrackHeight)
                    return i;
                y += PSXTimelineState.TrackHeight;
            }

            // Skip event lanes
            if (state.IsCutscene) y += PSXTimelineState.EventLaneHeight;
            if (state.SkinAnimEvents != null) y += PSXTimelineState.EventLaneHeight;

            // Add track row
            if (localPos.y >= y && localPos.y < y + PSXTimelineState.TrackHeight)
                return -1; // signal: add track

            return -2; // nothing
        }
    }
}
#endif
