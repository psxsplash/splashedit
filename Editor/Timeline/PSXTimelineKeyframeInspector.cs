#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SplashEdit.RuntimeCode;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Bottom panel of the PSX Timeline Editor showing properties of the selected
    /// keyframe or event. Handles per-type value editing.
    /// </summary>
    public static class PSXTimelineKeyframeInspector
    {
        private static Vector2 _scrollPos;

        public static void Draw(PSXTimelineState state)
        {
            var rect = state.KeyframeInspRect;

            // Background
            EditorGUI.DrawRect(rect, PSXEditorStyles.BackgroundMedium);
            // Top border
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), PSXEditorStyles.TextMuted);

            GUILayout.BeginArea(rect);

            // Collapse toggle header
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            string headerLabel = state.InspectorCollapsed ? "\u25B6 Properties" : "\u25BC Properties";
            if (GUILayout.Button(headerLabel, EditorStyles.toolbarButton, GUILayout.Width(100)))
                state.InspectorCollapsed = !state.InspectorCollapsed;
            GUILayout.FlexibleSpace();

            // Clip name and duration (always visible)
            state.ClipName = EditorGUILayout.TextField(state.ClipName, GUILayout.Width(150));
            EditorGUILayout.LabelField("Duration:", GUILayout.Width(55));
            float durSec = state.DurationFrames / 30f;
            EditorGUI.BeginChangeCheck();
            durSec = EditorGUILayout.FloatField(durSec, GUILayout.Width(50));
            if (EditorGUI.EndChangeCheck())
                state.DurationFrames = Mathf.Max(1, Mathf.RoundToInt(durSec * 30f));
            EditorGUILayout.LabelField("s", GUILayout.Width(12));

            EditorGUILayout.EndHorizontal();

            if (!state.InspectorCollapsed)
            {
                _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

                if (state.SelectedKeyframeIndex >= 0 && state.SelectedTrackIndex >= 0)
                {
                    DrawKeyframeProperties(state);
                }
                else if (state.SelectedEventType >= 0 && state.SelectedEventIndex >= 0)
                {
                    if (state.SelectedEventType == 0)
                        DrawAudioEventProperties(state);
                    else
                        DrawSkinAnimEventProperties(state);
                }
                else if (state.SelectedTrackIndex >= 0)
                {
                    DrawTrackProperties(state);
                }
                else
                {
                    EditorGUILayout.LabelField("Select a keyframe, event, or track to edit its properties.",
                        EditorStyles.centeredGreyMiniLabel);
                }

                EditorGUILayout.EndScrollView();
            }

            GUILayout.EndArea();
        }

        // =====================================================================
        // Keyframe Properties
        // =====================================================================

        private static void DrawKeyframeProperties(PSXTimelineState state)
        {
            var tracks = state.Tracks;
            if (tracks == null || state.SelectedTrackIndex >= tracks.Count) return;
            var track = tracks[state.SelectedTrackIndex];
            if (track.Keyframes == null || state.SelectedKeyframeIndex >= track.Keyframes.Count) return;

            var kf = track.Keyframes[state.SelectedKeyframeIndex];

            EditorGUILayout.LabelField($"Keyframe on {PSXTimelineDrawer.GetTrackLabel(track)}", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            // Frame / time
            EditorGUILayout.LabelField("Frame:", GUILayout.Width(42));
            int newFrame = EditorGUILayout.IntField(kf.Frame, GUILayout.Width(60));
            if (newFrame != kf.Frame)
                kf.Frame = Mathf.Clamp(newFrame, 0, state.DurationFrames);

            float kfSec = kf.Frame / 30f;
            EditorGUILayout.LabelField($"({kfSec:F2}s)", GUILayout.Width(55));

            // Interpolation
            EditorGUILayout.LabelField("Interp:", GUILayout.Width(40));
            kf.Interp = (PSXInterpMode)EditorGUILayout.EnumPopup(kf.Interp, GUILayout.Width(90));

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Value (type-dependent)
            switch (track.TrackType)
            {
                case PSXTrackType.ObjectActive:
                case PSXTrackType.UICanvasVisible:
                case PSXTrackType.UIElementVisible:
                {
                    string label = track.TrackType == PSXTrackType.ObjectActive ? "Active" : "Visible";
                    bool val = kf.Value.x > 0.5f;
                    val = EditorGUILayout.Toggle(label, val);
                    kf.Value = new Vector3(val ? 1f : 0f, 0, 0);
                    break;
                }
                case PSXTrackType.ObjectPosition:
                case PSXTrackType.CameraPosition:
                    kf.Value = EditorGUILayout.Vector3Field("Position", kf.Value);
                    break;
                case PSXTrackType.ObjectRotation:
                case PSXTrackType.CameraRotation:
                    kf.Value = EditorGUILayout.Vector3Field("Rotation", kf.Value);
                    break;
                case PSXTrackType.CameraH:
                {
                    float h = EditorGUILayout.Slider("H Register", kf.Value.x, 30f, 600f);
                    kf.Value = new Vector3(Mathf.Round(h), 0, 0);
                    float fov = 2f * Mathf.Atan(120f / h) * Mathf.Rad2Deg;
                    EditorGUILayout.LabelField($"  ~ {fov:F1} vertical FOV", EditorStyles.miniLabel);
                    break;
                }
                case PSXTrackType.UIProgress:
                {
                    float progress = EditorGUILayout.Slider("Progress %", kf.Value.x, 0f, 100f);
                    kf.Value = new Vector3(progress, 0, 0);
                    break;
                }
                case PSXTrackType.UIPosition:
                {
                    Vector2 pos = EditorGUILayout.Vector2Field("Position (px)",
                        new Vector2(kf.Value.x, kf.Value.y));
                    kf.Value = new Vector3(pos.x, pos.y, 0);
                    break;
                }
                case PSXTrackType.UIColor:
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("R", GUILayout.Width(14));
                    int r = EditorGUILayout.IntField(Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 0, 255), GUILayout.Width(40));
                    EditorGUILayout.LabelField("G", GUILayout.Width(14));
                    int g = EditorGUILayout.IntField(Mathf.Clamp(Mathf.RoundToInt(kf.Value.y), 0, 255), GUILayout.Width(40));
                    EditorGUILayout.LabelField("B", GUILayout.Width(14));
                    int b = EditorGUILayout.IntField(Mathf.Clamp(Mathf.RoundToInt(kf.Value.z), 0, 255), GUILayout.Width(40));
                    EditorGUILayout.EndHorizontal();
                    kf.Value = new Vector3(r, g, b);
                    break;
                }
                case PSXTrackType.RumbleSmall:
                {
                    bool rumbleOn = kf.Value.x > 0.5f;
                    rumbleOn = EditorGUILayout.Toggle("Motor On", rumbleOn);
                    kf.Value = new Vector3(rumbleOn ? 1f : 0f, 0, 0);
                    EditorGUILayout.LabelField("  Small motor (right side, high frequency, on/off only)", EditorStyles.miniLabel);
                    break;
                }
                case PSXTrackType.RumbleLarge:
                {
                    float speed = EditorGUILayout.Slider("Motor Speed", kf.Value.x, 0f, 255f);
                    kf.Value = new Vector3(Mathf.Round(speed), 0, 0);
                    EditorGUILayout.LabelField("  Large motor (left side, low frequency, 0-255 speed)", EditorStyles.miniLabel);
                    if (speed > 0 && speed < 80)
                        EditorGUILayout.HelpBox("Note: large motor typically starts spinning at ~80-96. Values below may not produce vibration.", MessageType.Info);
                    break;
                }
                case PSXTrackType.ObjectUVOffset:
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("U", GUILayout.Width(14));
                    int u = EditorGUILayout.IntField(Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 0, 255), GUILayout.Width(40));
                    EditorGUILayout.LabelField("V", GUILayout.Width(14));
                    int v = EditorGUILayout.IntField(Mathf.Clamp(Mathf.RoundToInt(kf.Value.y), 0, 255), GUILayout.Width(40));
                    EditorGUILayout.EndHorizontal();
                    kf.Value = new Vector3(u, v, 0);
                    break;
                }
                default:
                    kf.Value = EditorGUILayout.Vector3Field("Value", kf.Value);
                    break;
            }

            // Capture from scene buttons
            EditorGUILayout.Space(4);
            if (track.IsCameraTrack)
            {
                if (GUILayout.Button("Capture from Scene Camera", GUILayout.Width(200)))
                {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null)
                    {
                        if (track.TrackType == PSXTrackType.CameraPosition)
                            kf.Value = sv.camera.transform.position;
                        else if (track.TrackType == PSXTrackType.CameraRotation)
                            kf.Value = sv.camera.transform.eulerAngles;
                        else if (track.TrackType == PSXTrackType.CameraH)
                        {
                            float fovDeg = sv.camera.fieldOfView;
                            float half = Mathf.Clamp(fovDeg, 1f, 179f) * 0.5f * Mathf.Deg2Rad;
                            kf.Value = new Vector3(Mathf.Round(120f / Mathf.Tan(half)), 0, 0);
                        }
                    }
                }
            }
            else if (!track.IsUITrack &&
                (track.TrackType == PSXTrackType.ObjectPosition || track.TrackType == PSXTrackType.ObjectRotation))
            {
                if (!string.IsNullOrEmpty(track.ObjectName))
                {
                    if (GUILayout.Button($"Capture from '{track.ObjectName}'", GUILayout.Width(220)))
                    {
                        var go = GameObject.Find(track.ObjectName);
                        if (go != null)
                            kf.Value = track.TrackType == PSXTrackType.ObjectPosition
                                ? go.transform.position : go.transform.eulerAngles;
                    }
                }
            }
        }

        // =====================================================================
        // Track Properties (when track header selected, no keyframe)
        // =====================================================================

        private static void DrawTrackProperties(PSXTimelineState state)
        {
            var tracks = state.Tracks;
            if (tracks == null || state.SelectedTrackIndex >= tracks.Count) return;
            var track = tracks[state.SelectedTrackIndex];

            EditorGUILayout.LabelField("Track Properties", EditorStyles.boldLabel);

            track.TrackType = (PSXTrackType)EditorGUILayout.EnumPopup("Type", track.TrackType);

            // Filter camera tracks for animation clips
            if (!state.IsCutscene && track.IsCameraTrack)
            {
                EditorGUILayout.HelpBox("Camera tracks are not allowed in animations. Use a Cutscene instead.", MessageType.Warning);
            }

            if (track.IsCameraTrack)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField("Target", "(camera)");
                EditorGUI.EndDisabledGroup();
            }
            else if (track.IsVibrationTrack)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField("Target", "(controller)");
                EditorGUI.EndDisabledGroup();
            }
            else if (track.IsUITrack)
            {
                track.UICanvasName = EditorGUILayout.TextField("Canvas Name", track.UICanvasName);
                if (track.IsUIElementTrack)
                    track.UIElementName = EditorGUILayout.TextField("Element Name", track.UIElementName);
            }
            else
            {
                track.ObjectName = EditorGUILayout.TextField("Object Name", track.ObjectName);
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField($"{track.Keyframes?.Count ?? 0} keyframes", EditorStyles.miniLabel);
        }

        // =====================================================================
        // Audio Event Properties
        // =====================================================================

        private static void DrawAudioEventProperties(PSXTimelineState state)
        {
            var events = state.AudioEvents;
            if (events == null || state.SelectedEventIndex >= events.Count) return;
            var evt = events[state.SelectedEventIndex];

            EditorGUILayout.LabelField("Audio Event", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Frame:", GUILayout.Width(42));
            evt.Frame = Mathf.Max(0, EditorGUILayout.IntField(evt.Frame, GUILayout.Width(60)));
            float sec = evt.Frame / 30f;
            EditorGUILayout.LabelField($"({sec:F2}s)", GUILayout.Width(55));
            EditorGUILayout.EndHorizontal();

            evt.ClipName = EditorGUILayout.TextField("Clip Name", evt.ClipName);
            evt.Volume = EditorGUILayout.IntSlider("Volume", evt.Volume, 0, 128);
            evt.Pan = EditorGUILayout.IntSlider("Pan", evt.Pan, 0, 127);
        }

        // =====================================================================
        // Skin Anim Event Properties
        // =====================================================================

        private static void DrawSkinAnimEventProperties(PSXTimelineState state)
        {
            var events = state.SkinAnimEvents;
            if (events == null || state.SelectedEventIndex >= events.Count) return;
            var evt = events[state.SelectedEventIndex];

            EditorGUILayout.LabelField("Skin Anim Event", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Frame:", GUILayout.Width(42));
            evt.Frame = Mathf.Max(0, EditorGUILayout.IntField(evt.Frame, GUILayout.Width(60)));
            float sec = evt.Frame / 30f;
            EditorGUILayout.LabelField($"({sec:F2}s)", GUILayout.Width(55));
            EditorGUILayout.EndHorizontal();

            evt.TargetObjectName = EditorGUILayout.TextField("Target Object", evt.TargetObjectName);
            evt.ClipName = EditorGUILayout.TextField("Clip Name", evt.ClipName);
            evt.Loop = EditorGUILayout.Toggle("Loop", evt.Loop);

            // Validation
            if (!string.IsNullOrEmpty(evt.TargetObjectName))
            {
                var skinnedExporters = Object.FindObjectsByType<PSXSkinnedObjectExporter>(FindObjectsSortMode.None);
                bool found = false;
                foreach (var se in skinnedExporters)
                {
                    if (se.gameObject.name == evt.TargetObjectName)
                    {
                        found = true;
                        if (!string.IsNullOrEmpty(evt.ClipName) && se.AnimationClips != null)
                        {
                            bool clipFound = false;
                            foreach (var ac in se.AnimationClips)
                                if (ac != null && ac.name == evt.ClipName) { clipFound = true; break; }
                            if (!clipFound)
                                EditorGUILayout.HelpBox($"No AnimationClip named '{evt.ClipName}' on '{evt.TargetObjectName}'.", MessageType.Error);
                        }
                        break;
                    }
                }
                if (!found)
                    EditorGUILayout.HelpBox($"No PSXSkinnedObjectExporter found for '{evt.TargetObjectName}' in scene.", MessageType.Error);
            }
        }
    }
}
#endif
