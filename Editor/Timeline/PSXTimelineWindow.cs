#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using SplashEdit.RuntimeCode;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Dedicated EditorWindow for editing PSXAnimationClip and PSXCutsceneClip assets
    /// with a visual timeline layout.
    /// </summary>
    public class PSXTimelineWindow : EditorWindow
    {
        private PSXTimelineState _state = new PSXTimelineState();
        private PSXTimelinePreview _preview = new PSXTimelinePreview();

        // =====================================================================
        // Window Lifecycle
        // =====================================================================

        [MenuItem("PlayStation 1/Timeline Editor")]
        public static void ShowWindow()
        {
            var window = GetWindow<PSXTimelineWindow>();
            window.titleContent = new GUIContent("PSX Timeline");
            window.minSize = new Vector2(600, 300);
            window.Show();
        }

        public static void Open(ScriptableObject clip)
        {
            var window = GetWindow<PSXTimelineWindow>();
            window.titleContent = new GUIContent("PSX Timeline");
            window.minSize = new Vector2(600, 300);
            if (window._state.IsPreviewing)
                window._preview.StopPreview(window._state);
            window._state.SetClip(clip);
            window.Show();
            window.Focus();
        }

        [OnOpenAsset]
        public static bool OnOpenAsset(int instanceID, int line)
        {
            var obj = EditorUtility.InstanceIDToObject(instanceID);
            if (obj is PSXAnimationClip || obj is PSXCutsceneClip)
            {
                Open((ScriptableObject)obj);
                return true;
            }
            return false;
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            if (_state.IsPreviewing)
                _preview.StopPreview(_state);
        }

        private void OnEditorUpdate()
        {
            if (!_state.IsPlaying) return;

            float elapsed = (float)(EditorApplication.timeSinceStartup - _state.PlayStartEditorTime);
            _state.PlayheadFrame = _state.PlayStartFrame + elapsed * 30f;

            if (_state.PlayheadFrame >= _state.DurationFrames)
            {
                _state.PlayheadFrame = _state.DurationFrames;
                _state.IsPlaying = false;
            }

            if (_state.IsPreviewing)
                _preview.ApplyPreview(_state);

            Repaint();
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            // Drop target: accept clip assets dragged onto the window
            HandleDragAndDrop();

            if (_state.Clip == null)
            {
                DrawNoClipMessage();
                return;
            }

            // Validate clip still exists
            if (_state.Clip == null || !_state.Clip)
            {
                _state.Clip = null;
                Repaint();
                return;
            }

            ComputeLayout();
            HandleInput();

            _state.RequestedAction = PSXTimelineState.ToolbarAction.None;
            PSXTimelineDrawer.DrawToolbar(_state);
            HandleToolbarAction();

            PSXTimelineDrawer.DrawTimeRuler(_state);
            PSXTimelineDrawer.DrawTrackHeaders(_state);
            PSXTimelineDrawer.DrawTrackLanes(_state);

            // Keyframe inspector at bottom
            PSXTimelineKeyframeInspector.Draw(_state);

            if (_state.IsPlaying)
                Repaint();

            if (GUI.changed)
                EditorUtility.SetDirty(_state.Clip);
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private void ComputeLayout()
        {
            float w = position.width;
            float h = position.height;
            float inspH = _state.InspectorCollapsed ? 20f : _state.InspectorHeight;

            _state.ToolbarRect = new Rect(0, 0, w, PSXTimelineState.ToolbarHeight);

            float bodyTop = PSXTimelineState.ToolbarHeight;
            float bodyH = h - PSXTimelineState.ToolbarHeight - inspH;
            if (bodyH < 60) bodyH = 60;

            _state.TimeRulerRect = new Rect(PSXTimelineState.TrackHeaderWidth, bodyTop,
                w - PSXTimelineState.TrackHeaderWidth, PSXTimelineState.TimeRulerHeight);

            float tracksTop = bodyTop + PSXTimelineState.TimeRulerHeight;
            float tracksH = bodyH - PSXTimelineState.TimeRulerHeight;

            _state.TrackHeaderRect = new Rect(0, tracksTop,
                PSXTimelineState.TrackHeaderWidth, tracksH);

            _state.TimelineAreaRect = new Rect(PSXTimelineState.TrackHeaderWidth, tracksTop,
                w - PSXTimelineState.TrackHeaderWidth, tracksH);

            _state.KeyframeInspRect = new Rect(0, bodyTop + bodyH, w, inspH);
        }

        // =====================================================================
        // Toolbar Action Handling
        // =====================================================================

        private void HandleToolbarAction()
        {
            switch (_state.RequestedAction)
            {
                case PSXTimelineState.ToolbarAction.Play:
                    if (!_state.IsPreviewing)
                    {
                        _preview.StartPreview(_state);
                        _state.IsPreviewing = true;
                    }
                    _preview.ResetAudioEvents();
                    _state.IsPlaying = true;
                    _state.PlayStartEditorTime = EditorApplication.timeSinceStartup;
                    _state.PlayStartFrame = _state.PlayheadFrame;
                    // Seed fired set for events before current playhead
                    _preview.SeedFiredEventsUpTo(_state);
                    break;

                case PSXTimelineState.ToolbarAction.Pause:
                    _state.IsPlaying = false;
                    break;

                case PSXTimelineState.ToolbarAction.Stop:
                    _state.IsPlaying = false;
                    _state.PlayheadFrame = 0;
                    if (_state.IsPreviewing)
                    {
                        _preview.StopPreview(_state);
                        _state.IsPreviewing = false;
                    }
                    break;

                case PSXTimelineState.ToolbarAction.EndPreview:
                    _state.IsPlaying = false;
                    if (_state.IsPreviewing)
                    {
                        _preview.StopPreview(_state);
                        _state.IsPreviewing = false;
                    }
                    break;
            }
            _state.RequestedAction = PSXTimelineState.ToolbarAction.None;
        }

        // =====================================================================
        // Input Handling
        // =====================================================================

        private void HandleInput()
        {
            Event e = Event.current;

            // Keyboard shortcuts (handle regardless of mouse position)
            if (e.type == EventType.KeyDown)
            {
                HandleKeyDown(e);
                return;
            }

            // Mouse events on time ruler
            if (_state.TimeRulerRect.Contains(e.mousePosition))
            {
                HandleRulerInput(e);
                return;
            }

            // Mouse events on timeline area
            if (_state.TimelineAreaRect.Contains(e.mousePosition))
            {
                HandleTimelineInput(e);
                return;
            }

            // Mouse events on track headers
            if (_state.TrackHeaderRect.Contains(e.mousePosition))
            {
                HandleTrackHeaderInput(e);
                return;
            }

            // Release drag anywhere
            if (e.type == EventType.MouseUp)
            {
                _state.IsDraggingKeyframe = false;
                _state.IsDraggingPlayhead = false;
            }
        }

        private void HandleKeyDown(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Space:
                    _state.RequestedAction = _state.IsPlaying
                        ? PSXTimelineState.ToolbarAction.Pause
                        : PSXTimelineState.ToolbarAction.Play;
                    HandleToolbarAction();
                    e.Use();
                    Repaint();
                    break;

                case KeyCode.Delete:
                case KeyCode.Backspace:
                    if (!EditorGUIUtility.editingTextField)
                    {
                        if (_state.SelectedEventType >= 0 && _state.SelectedEventIndex >= 0)
                            DeleteSelectedEvent();
                        else
                            DeleteSelectedKeyframe();
                        e.Use();
                        Repaint();
                    }
                    break;
            }
        }

        private void HandleRulerInput(Event e)
        {
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                float localX = e.mousePosition.x - _state.TimeRulerRect.x;
                float frame = _state.PixelXToFrame(localX);
                _state.PlayheadFrame = Mathf.Clamp(Mathf.Round(frame), 0, _state.DurationFrames);
                _state.IsDraggingPlayhead = true;

                if (!_state.IsPreviewing)
                {
                    _preview.StartPreview(_state);
                    _state.IsPreviewing = true;
                }
                _preview.ApplyPreview(_state);

                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDrag && _state.IsDraggingPlayhead)
            {
                float localX = e.mousePosition.x - _state.TimeRulerRect.x;
                float frame = _state.PixelXToFrame(localX);
                _state.PlayheadFrame = Mathf.Clamp(Mathf.Round(frame), 0, _state.DurationFrames);
                _state.IsPlaying = false;

                if (_state.IsPreviewing)
                    _preview.ApplyPreview(_state);

                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseUp)
            {
                _state.IsDraggingPlayhead = false;
            }

            HandleZoomPan(e);
        }

        private void HandleTimelineInput(Event e)
        {
            Vector2 localPos = e.mousePosition - new Vector2(_state.TimelineAreaRect.x, _state.TimelineAreaRect.y);

            if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 2)
            {
                // Double-click: add keyframe at position (checked BEFORE single click)
                HandleDoubleClickAddKeyframe(localPos);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDown && e.button == 0)
            {
                // Check keyframe hit
                var (ti, ki) = PSXTimelineDrawer.HitTestKeyframe(_state, localPos);
                if (ti >= 0 && ki >= 0)
                {
                    // Select keyframe
                    if (e.shift)
                    {
                        _state.MultiSelection.Add((ti, ki));
                    }
                    else
                    {
                        _state.MultiSelection.Clear();
                        _state.SelectedTrackIndex = ti;
                        _state.SelectedKeyframeIndex = ki;
                        _state.SelectedEventType = -1;
                        _state.SelectedEventIndex = -1;
                    }

                    // Start drag
                    _state.IsDraggingKeyframe = true;
                    _state.DragOriginalFrame = _state.Tracks[ti].Keyframes[ki].Frame;
                    Undo.RecordObject(_state.Clip, "Move Keyframe");

                    e.Use();
                    Repaint();
                    return;
                }

                // Check event hit
                var (evtType, evtIdx) = PSXTimelineDrawer.HitTestEvent(_state, localPos);
                if (evtType >= 0 && evtIdx >= 0)
                {
                    _state.SelectedEventType = evtType;
                    _state.SelectedEventIndex = evtIdx;
                    _state.SelectedTrackIndex = -1;
                    _state.SelectedKeyframeIndex = -1;
                    _state.MultiSelection.Clear();
                    e.Use();
                    Repaint();
                    return;
                }

                // Click on empty area - deselect
                _state.ClearSelection();
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDrag && _state.IsDraggingKeyframe)
            {
                // Drag keyframe
                if (_state.SelectedTrackIndex >= 0 && _state.SelectedKeyframeIndex >= 0)
                {
                    float frame = _state.PixelXToFrame(localPos.x);
                    int newFrame = Mathf.Clamp(Mathf.RoundToInt(frame), 0, _state.DurationFrames);
                    var kf = _state.Tracks[_state.SelectedTrackIndex].Keyframes[_state.SelectedKeyframeIndex];
                    if (kf.Frame != newFrame)
                    {
                        kf.Frame = newFrame;
                        EditorUtility.SetDirty(_state.Clip);
                    }
                    e.Use();
                    Repaint();
                }
            }
            else if (e.type == EventType.MouseUp)
            {
                // Re-sort keyframes after drag completes
                if (_state.IsDraggingKeyframe && _state.SelectedTrackIndex >= 0)
                {
                    var keyframes = _state.Tracks[_state.SelectedTrackIndex].Keyframes;
                    if (keyframes != null && _state.SelectedKeyframeIndex >= 0 && _state.SelectedKeyframeIndex < keyframes.Count)
                    {
                        var draggedKf = keyframes[_state.SelectedKeyframeIndex];
                        keyframes.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                        _state.SelectedKeyframeIndex = keyframes.IndexOf(draggedKf);
                    }
                }
                _state.IsDraggingKeyframe = false;
            }
            else if (e.type == EventType.ContextClick)
            {
                ShowTimelineContextMenu(localPos);
                e.Use();
            }

            HandleZoomPan(e);
        }

        private void HandleTrackHeaderInput(Event e)
        {
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                Vector2 localPos = e.mousePosition - new Vector2(_state.TrackHeaderRect.x, _state.TrackHeaderRect.y);
                int hit = PSXTimelineDrawer.HitTestTrackHeader(_state, localPos);

                if (hit == -1) // "Add Track" row
                {
                    ShowAddTrackMenu();
                    e.Use();
                }
                else if (hit >= 0)
                {
                    _state.SelectedTrackIndex = hit;
                    _state.SelectedKeyframeIndex = -1;
                    _state.SelectedEventType = -1;
                    _state.SelectedEventIndex = -1;
                    e.Use();
                    Repaint();
                }
            }
            else if (e.type == EventType.ContextClick)
            {
                Vector2 localPos = e.mousePosition - new Vector2(_state.TrackHeaderRect.x, _state.TrackHeaderRect.y);
                int hit = PSXTimelineDrawer.HitTestTrackHeader(_state, localPos);
                if (hit == -1)
                {
                    ShowAddTrackMenu();
                    e.Use();
                }
                else if (hit >= 0)
                {
                    ShowTrackHeaderContextMenu(hit);
                    e.Use();
                }
            }
        }

        private void HandleZoomPan(Event e)
        {
            if (e.type == EventType.ScrollWheel)
            {
                if (e.shift)
                {
                    // Vertical scroll
                    _state.ScrollY = Mathf.Max(0, _state.ScrollY + e.delta.y * 15f);
                }
                else
                {
                    // Zoom centered on cursor
                    float cursorLocalX = e.mousePosition.x - _state.TimelineAreaRect.x;
                    float cursorFrame = _state.PixelXToFrame(cursorLocalX);

                    float zoomDelta = -e.delta.y * 0.3f;
                    _state.PixelsPerFrame = Mathf.Clamp(_state.PixelsPerFrame + zoomDelta, 1f, 20f);

                    // Adjust scroll so cursor stays over the same frame
                    _state.ScrollX = cursorFrame * _state.PixelsPerFrame - cursorLocalX;
                    _state.ScrollX = Mathf.Max(0, _state.ScrollX);
                }

                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDrag && (e.button == 2 || (e.button == 0 && e.alt)))
            {
                // Pan
                _state.ScrollX = Mathf.Max(0, _state.ScrollX - e.delta.x);
                _state.ScrollY = Mathf.Max(0, _state.ScrollY - e.delta.y);
                e.Use();
                Repaint();
            }
        }

        // =====================================================================
        // Context Menus
        // =====================================================================

        private void ShowTimelineContextMenu(Vector2 localPos)
        {
            var menu = new GenericMenu();
            float frame = _state.PixelXToFrame(localPos.x);
            int targetFrame = Mathf.Clamp(Mathf.RoundToInt(frame), 0, _state.DurationFrames);
            var (eventType, _) = PSXTimelineDrawer.HitTestEvent(_state, localPos);
            int eventLaneType = GetEventLaneTypeAtY(localPos.y);

            // Determine which track lane was clicked
            int trackIdx = Mathf.FloorToInt((localPos.y + _state.ScrollY) / PSXTimelineState.TrackHeight);
            int trackCount = _state.Tracks?.Count ?? 0;

            if (trackIdx >= 0 && trackIdx < trackCount)
            {
                menu.AddItem(new GUIContent($"Add Keyframe at frame {targetFrame}"), false, () =>
                {
                    var track = _state.Tracks[trackIdx];
                    if (track.Keyframes == null) track.Keyframes = new List<PSXKeyframe>();
                    if (track.Keyframes.Count < 64)
                    {
                        track.Keyframes.Add(new PSXKeyframe { Frame = targetFrame, Interp = PSXInterpMode.Linear });
                        track.Keyframes.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                        EditorUtility.SetDirty(_state.Clip);
                    }
                });
            }

            if ((eventType == 0 || eventLaneType == 0) && _state.IsCutscene && _state.AudioEvents != null)
            {
                menu.AddItem(new GUIContent($"Add Audio Event at frame {targetFrame}"), false, () =>
                {
                    Undo.RecordObject(_state.Clip, "Add Audio Event");
                    _state.AudioEvents.Add(new PSXAudioEvent
                    {
                        Frame = targetFrame,
                        ClipName = "",
                        Volume = 128,
                        Pan = 64
                    });
                    _state.AudioEvents.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                    _state.SelectedEventType = 0;
                    _state.SelectedEventIndex = _state.AudioEvents.FindIndex(evt => evt.Frame == targetFrame);
                    _state.SelectedTrackIndex = -1;
                    _state.SelectedKeyframeIndex = -1;
                    EditorUtility.SetDirty(_state.Clip);
                });
            }

            if ((eventType == 1 || eventLaneType == 1) && _state.SkinAnimEvents != null)
            {
                menu.AddItem(new GUIContent($"Add Skin Anim Event at frame {targetFrame}"), false, () =>
                {
                    Undo.RecordObject(_state.Clip, "Add Skin Anim Event");
                    _state.SkinAnimEvents.Add(new PSXSkinAnimEvent
                    {
                        Frame = targetFrame,
                        TargetObjectName = "",
                        ClipName = "",
                        Loop = false
                    });
                    _state.SkinAnimEvents.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                    _state.SelectedEventType = 1;
                    _state.SelectedEventIndex = _state.SkinAnimEvents.FindIndex(evt => evt.Frame == targetFrame);
                    _state.SelectedTrackIndex = -1;
                    _state.SelectedKeyframeIndex = -1;
                    EditorUtility.SetDirty(_state.Clip);
                });
            }

            if (_state.SelectedKeyframeIndex >= 0)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete Selected Keyframe"), false, DeleteSelectedKeyframe);

                menu.AddSeparator("");
                foreach (PSXInterpMode mode in System.Enum.GetValues(typeof(PSXInterpMode)))
                {
                    var m = mode;
                    bool isActive = _state.SelectedTrackIndex >= 0 && _state.SelectedKeyframeIndex >= 0 &&
                        _state.Tracks[_state.SelectedTrackIndex].Keyframes[_state.SelectedKeyframeIndex].Interp == m;
                    menu.AddItem(new GUIContent($"Interpolation/{m}"), isActive, () =>
                    {
                        if (_state.SelectedTrackIndex >= 0 && _state.SelectedKeyframeIndex >= 0)
                        {
                            _state.Tracks[_state.SelectedTrackIndex].Keyframes[_state.SelectedKeyframeIndex].Interp = m;
                            EditorUtility.SetDirty(_state.Clip);
                        }
                    });
                }
            }

            if (_state.SelectedEventType >= 0 && _state.SelectedEventIndex >= 0)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete Selected Event"), false, DeleteSelectedEvent);
            }

            menu.ShowAsContext();
        }

        private int GetEventLaneTypeAtY(float localY)
        {
            float y = -_state.ScrollY + (_state.Tracks?.Count ?? 0) * PSXTimelineState.TrackHeight;

            if (_state.IsCutscene)
            {
                if (localY >= y && localY < y + PSXTimelineState.EventLaneHeight)
                    return 0;
                y += PSXTimelineState.EventLaneHeight;
            }

            if (_state.SkinAnimEvents != null)
            {
                if (localY >= y && localY < y + PSXTimelineState.EventLaneHeight)
                    return 1;
            }

            return -1;
        }

        private void ShowTrackHeaderContextMenu(int trackIdx)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Remove Track"), false, () =>
            {
                if (_state.Tracks != null && trackIdx < _state.Tracks.Count)
                {
                    _state.Tracks.RemoveAt(trackIdx);
                    _state.ClearSelection();
                    EditorUtility.SetDirty(_state.Clip);
                    Repaint();
                }
            });
            menu.ShowAsContext();
        }

        private void ShowAddTrackMenu()
        {
            if (_state.Tracks != null && _state.Tracks.Count >= 8)
            {
                Debug.LogWarning("Maximum 8 tracks per clip.");
                return;
            }

            var menu = new GenericMenu();

            // ── Camera tracks (cutscene only) ──
            if (_state.IsCutscene)
            {
                menu.AddItem(new GUIContent("Camera/Position"), false, () => AddTrack(PSXTrackType.CameraPosition));
                menu.AddItem(new GUIContent("Camera/Rotation"), false, () => AddTrack(PSXTrackType.CameraRotation));
                menu.AddItem(new GUIContent("Camera/FOV (H)"), false, () => AddTrack(PSXTrackType.CameraH));
            }

            // ── Object tracks: submenu per scene object ──
            var objects = Object.FindObjectsByType<PSXObjectExporter>(FindObjectsSortMode.None);
            if (objects.Length > 0)
            {
                foreach (var obj in objects)
                {
                    string n = obj.gameObject.name;
                    menu.AddItem(new GUIContent($"Object/{n}/Position"), false,
                        () => AddTrack(PSXTrackType.ObjectPosition, objectName: n));
                    menu.AddItem(new GUIContent($"Object/{n}/Rotation"), false,
                        () => AddTrack(PSXTrackType.ObjectRotation, objectName: n));
                    menu.AddItem(new GUIContent($"Object/{n}/Active"), false,
                        () => AddTrack(PSXTrackType.ObjectActive, objectName: n));
                    menu.AddItem(new GUIContent($"Object/{n}/UV Offset"), false,
                        () => AddTrack(PSXTrackType.ObjectUVOffset, objectName: n));
                }
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Object/(no PSXObjectExporters in scene)"));
            }

            // ── UI Canvas Visible track ──
            var canvases = Object.FindObjectsByType<PSXCanvas>(FindObjectsSortMode.None);
            if (canvases.Length > 0)
            {
                foreach (var canvas in canvases)
                {
                    string cn = canvas.CanvasName;
                    menu.AddItem(new GUIContent($"UI Canvas/{cn}/Visible"), false,
                        () => AddTrack(PSXTrackType.UICanvasVisible, canvasName: cn));

                    // Gather all UI elements under this canvas
                    var elements = new List<(string name, string type)>();
                    foreach (var img in canvas.GetComponentsInChildren<PSXUIImage>(true))
                        elements.Add((img.ElementName, "Image"));
                    foreach (var box in canvas.GetComponentsInChildren<PSXUIBox>(true))
                        elements.Add((box.ElementName, "Box"));
                    foreach (var txt in canvas.GetComponentsInChildren<PSXUIText>(true))
                        elements.Add((txt.ElementName, "Text"));
                    foreach (var bar in canvas.GetComponentsInChildren<PSXUIProgressBar>(true))
                        elements.Add((bar.ElementName, "Progress"));

                    if (elements.Count > 0)
                    {
                        foreach (var (elName, elType) in elements)
                        {
                            string prefix = $"UI Canvas/{cn}/{elName} ({elType})";
                            menu.AddItem(new GUIContent($"{prefix}/Visible"), false,
                                () => AddTrack(PSXTrackType.UIElementVisible, canvasName: cn, elementName: elName));
                            menu.AddItem(new GUIContent($"{prefix}/Position"), false,
                                () => AddTrack(PSXTrackType.UIPosition, canvasName: cn, elementName: elName));
                            menu.AddItem(new GUIContent($"{prefix}/Color"), false,
                                () => AddTrack(PSXTrackType.UIColor, canvasName: cn, elementName: elName));

                            // Progress only for progress bars
                            if (elType == "Progress")
                            {
                                menu.AddItem(new GUIContent($"{prefix}/Progress"), false,
                                    () => AddTrack(PSXTrackType.UIProgress, canvasName: cn, elementName: elName));
                            }
                        }
                    }
                    else
                    {
                        menu.AddDisabledItem(new GUIContent($"UI Canvas/{cn}/(no UI elements)"));
                    }
                }
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("UI Canvas/(no PSXCanvas in scene)"));
            }

            // ── Rumble tracks ──
            menu.AddItem(new GUIContent("Rumble/Small Motor"), false, () => AddTrack(PSXTrackType.RumbleSmall));
            menu.AddItem(new GUIContent("Rumble/Large Motor"), false, () => AddTrack(PSXTrackType.RumbleLarge));

            menu.ShowAsContext();
        }

        private void AddTrack(PSXTrackType type, string objectName = "", string canvasName = "", string elementName = "")
        {
            if (_state.Tracks == null) return;
            _state.Tracks.Add(new PSXCutsceneTrack
            {
                TrackType = type,
                ObjectName = objectName,
                UICanvasName = canvasName,
                UIElementName = elementName,
            });
            EditorUtility.SetDirty(_state.Clip);
            Repaint();
        }

        // =====================================================================
        // Actions
        // =====================================================================

        private void DeleteSelectedKeyframe()
        {
            if (_state.SelectedTrackIndex >= 0 && _state.SelectedKeyframeIndex >= 0)
            {
                var track = _state.Tracks[_state.SelectedTrackIndex];
                if (track.Keyframes != null && _state.SelectedKeyframeIndex < track.Keyframes.Count)
                {
                    track.Keyframes.RemoveAt(_state.SelectedKeyframeIndex);
                    _state.ClearSelection();
                    EditorUtility.SetDirty(_state.Clip);
                }
            }

            // Also delete from multi-selection
            if (_state.MultiSelection.Count > 0)
            {
                // Sort descending so removal doesn't shift indices
                var sorted = new List<(int track, int kf)>(_state.MultiSelection);
                sorted.Sort((a, b) => b.kf.CompareTo(a.kf) != 0 ? b.kf.CompareTo(a.kf) : b.track.CompareTo(a.track));

                foreach (var (ti, ki) in sorted)
                {
                    if (ti < _state.Tracks.Count && ki < _state.Tracks[ti].Keyframes.Count)
                        _state.Tracks[ti].Keyframes.RemoveAt(ki);
                }
                _state.MultiSelection.Clear();
                EditorUtility.SetDirty(_state.Clip);
            }
        }

        private void DeleteSelectedEvent()
        {
            if (_state.SelectedEventType == 0 && _state.AudioEvents != null &&
                _state.SelectedEventIndex >= 0 && _state.SelectedEventIndex < _state.AudioEvents.Count)
            {
                Undo.RecordObject(_state.Clip, "Delete Audio Event");
                _state.AudioEvents.RemoveAt(_state.SelectedEventIndex);
                _state.ClearSelection();
                EditorUtility.SetDirty(_state.Clip);
                return;
            }

            if (_state.SelectedEventType == 1 && _state.SkinAnimEvents != null &&
                _state.SelectedEventIndex >= 0 && _state.SelectedEventIndex < _state.SkinAnimEvents.Count)
            {
                Undo.RecordObject(_state.Clip, "Delete Skin Anim Event");
                _state.SkinAnimEvents.RemoveAt(_state.SelectedEventIndex);
                _state.ClearSelection();
                EditorUtility.SetDirty(_state.Clip);
            }
        }

        private void HandleDoubleClickAddKeyframe(Vector2 localPos)
        {
            int trackIdx = Mathf.FloorToInt((localPos.y + _state.ScrollY) / PSXTimelineState.TrackHeight);
            int trackCount = _state.Tracks?.Count ?? 0;

            if (trackIdx >= 0 && trackIdx < trackCount)
            {
                var track = _state.Tracks[trackIdx];
                if (track.Keyframes == null) track.Keyframes = new List<PSXKeyframe>();
                if (track.Keyframes.Count >= 64) return;

                float frame = _state.PixelXToFrame(localPos.x);
                int targetFrame = Mathf.Clamp(Mathf.RoundToInt(frame), 0, _state.DurationFrames);

                track.Keyframes.Add(new PSXKeyframe
                {
                    Frame = targetFrame,
                    Value = Vector3.zero,
                    Interp = PSXInterpMode.Linear
                });
                track.Keyframes.Sort((a, b) => a.Frame.CompareTo(b.Frame));

                // Select the new keyframe
                int newIdx = track.Keyframes.FindIndex(kf => kf.Frame == targetFrame);
                _state.SelectedTrackIndex = trackIdx;
                _state.SelectedKeyframeIndex = newIdx >= 0 ? newIdx : track.Keyframes.Count - 1;
                _state.SelectedEventType = -1;
                _state.SelectedEventIndex = -1;

                EditorUtility.SetDirty(_state.Clip);
            }
        }

        // =====================================================================
        // Drag and Drop
        // =====================================================================

        private void HandleDragAndDrop()
        {
            Event e = Event.current;
            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                bool valid = false;
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is PSXAnimationClip || obj is PSXCutsceneClip)
                    {
                        valid = true;
                        break;
                    }
                }

                if (valid)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    if (e.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (var obj in DragAndDrop.objectReferences)
                        {
                            if (obj is PSXAnimationClip || obj is PSXCutsceneClip)
                            {
                                if (_state.IsPreviewing)
                                    _preview.StopPreview(_state);
                                _state.SetClip((ScriptableObject)obj);
                                break;
                            }
                        }
                    }
                    e.Use();
                }
            }
        }

        // =====================================================================
        // Empty State
        // =====================================================================

        private void DrawNoClipMessage()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), PSXEditorStyles.BackgroundDark);
            var style = new GUIStyle(EditorStyles.largeLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
            };
            style.normal.textColor = PSXEditorStyles.TextMuted;
            GUI.Label(new Rect(0, 0, position.width, position.height),
                "Drag a PSX Animation Clip or Cutscene Clip here\nor select one and use the Open in Timeline button",
                style);

            // Also accept object field
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var newClip = EditorGUILayout.ObjectField("Clip", null, typeof(ScriptableObject), false, GUILayout.Width(300));
            if (newClip != null && (newClip is PSXAnimationClip || newClip is PSXCutsceneClip))
                _state.SetClip((ScriptableObject)newClip);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }
    }
}
#endif
