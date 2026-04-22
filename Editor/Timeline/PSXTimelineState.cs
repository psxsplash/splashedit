#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// All mutable state for the PSX Timeline Editor window.
    /// Separated from the window so drawing and input can share it cleanly.
    /// </summary>
    public class PSXTimelineState
    {
        // ── Clip being edited ──
        public ScriptableObject Clip;
        public bool IsCutscene;

        // ── View state ──
        public float PixelsPerFrame = 5f;
        public float ScrollX;
        public float ScrollY;
        public const float TrackHeight = 28f;
        public const float EventLaneHeight = 24f;
        public const float TrackHeaderWidth = 180f;
        public const float TimeRulerHeight = 24f;
        public const float ToolbarHeight = 24f;
        public const float MinInspectorHeight = 120f;
        public const float DefaultInspectorHeight = 180f;
        public float InspectorHeight = DefaultInspectorHeight;
        public bool InspectorCollapsed;

        // ── Selection ──
        public int SelectedTrackIndex = -1;
        public int SelectedKeyframeIndex = -1;
        /// <summary>0 = audio event, 1 = skin anim event</summary>
        public int SelectedEventType = -1;
        public int SelectedEventIndex = -1;
        public HashSet<(int track, int kf)> MultiSelection = new HashSet<(int, int)>();

        // ── Transport / Playhead ──
        public float PlayheadFrame;
        public bool IsPlaying;
        public bool IsPreviewing;
        public double PlayStartEditorTime;
        public float PlayStartFrame;

        // ── Drag state ──
        public bool IsDraggingKeyframe;
        public bool IsDraggingPlayhead;
        public int DragOriginalFrame;

        // ── Toolbar action (set by drawer, consumed by window) ──
        public enum ToolbarAction { None, Play, Pause, Stop, EndPreview }
        public ToolbarAction RequestedAction;

        // ── Layout rects (computed each OnGUI) ──
        public Rect ToolbarRect;
        public Rect TrackHeaderRect;
        public Rect TimeRulerRect;
        public Rect TimelineAreaRect;
        public Rect KeyframeInspRect;

        // ── Accessors that work for both clip types ──

        public int DurationFrames
        {
            get
            {
                if (Clip is RuntimeCode.PSXCutsceneClip cc) return cc.DurationFrames;
                if (Clip is RuntimeCode.PSXAnimationClip ac) return ac.DurationFrames;
                return 90;
            }
            set
            {
                if (Clip is RuntimeCode.PSXCutsceneClip cc) cc.DurationFrames = value;
                else if (Clip is RuntimeCode.PSXAnimationClip ac) ac.DurationFrames = value;
            }
        }

        public string ClipName
        {
            get
            {
                if (Clip is RuntimeCode.PSXCutsceneClip cc) return cc.CutsceneName;
                if (Clip is RuntimeCode.PSXAnimationClip ac) return ac.AnimationName;
                return "";
            }
            set
            {
                if (Clip is RuntimeCode.PSXCutsceneClip cc) cc.CutsceneName = value;
                else if (Clip is RuntimeCode.PSXAnimationClip ac) ac.AnimationName = value;
            }
        }

        public List<RuntimeCode.PSXCutsceneTrack> Tracks
        {
            get
            {
                if (Clip is RuntimeCode.PSXCutsceneClip cc) return cc.Tracks;
                if (Clip is RuntimeCode.PSXAnimationClip ac) return ac.Tracks;
                return null;
            }
        }

        public List<RuntimeCode.PSXSkinAnimEvent> SkinAnimEvents
        {
            get
            {
                if (Clip is RuntimeCode.PSXCutsceneClip cc) return cc.SkinAnimEvents;
                if (Clip is RuntimeCode.PSXAnimationClip ac) return ac.SkinAnimEvents;
                return null;
            }
        }

        public List<RuntimeCode.PSXAudioEvent> AudioEvents
        {
            get
            {
                if (Clip is RuntimeCode.PSXCutsceneClip cc) return cc.AudioEvents;
                return null;
            }
        }

        // ── Coordinate conversion ──

        /// <summary>Convert a frame number to a pixel X offset within the timeline area.</summary>
        public float FrameToPixelX(float frame) => frame * PixelsPerFrame - ScrollX;

        /// <summary>Convert a pixel X offset within the timeline area to a frame number.</summary>
        public float PixelXToFrame(float px) => (px + ScrollX) / PixelsPerFrame;

        /// <summary>Get the visible frame range in the timeline area.</summary>
        public (int start, int end) GetVisibleFrameRange()
        {
            int start = Mathf.Max(0, Mathf.FloorToInt(ScrollX / PixelsPerFrame));
            int end = Mathf.CeilToInt((ScrollX + TimelineAreaRect.width) / PixelsPerFrame);
            return (start, end);
        }

        // ── Clip setup ──

        /// <summary>
        /// Set the clip to edit. Caller must stop preview before calling this
        /// if IsPreviewing is true (state can't call preview directly).
        /// </summary>
        public void SetClip(ScriptableObject clip)
        {
            Clip = clip;
            IsCutscene = clip is RuntimeCode.PSXCutsceneClip;

            if (clip is RuntimeCode.PSXCutsceneClip cc)
            {
                if (cc.Tracks == null) cc.Tracks = new List<RuntimeCode.PSXCutsceneTrack>();
                if (cc.AudioEvents == null) cc.AudioEvents = new List<RuntimeCode.PSXAudioEvent>();
                if (cc.SkinAnimEvents == null) cc.SkinAnimEvents = new List<RuntimeCode.PSXSkinAnimEvent>();
            }
            else if (clip is RuntimeCode.PSXAnimationClip ac)
            {
                if (ac.Tracks == null) ac.Tracks = new List<RuntimeCode.PSXCutsceneTrack>();
                if (ac.SkinAnimEvents == null) ac.SkinAnimEvents = new List<RuntimeCode.PSXSkinAnimEvent>();
            }

            ClearSelection();
            PlayheadFrame = 0;
            IsPlaying = false;
            IsPreviewing = false;
        }

        public void ClearSelection()
        {
            SelectedTrackIndex = -1;
            SelectedKeyframeIndex = -1;
            SelectedEventType = -1;
            SelectedEventIndex = -1;
            MultiSelection.Clear();
        }

        public bool HasSelection => SelectedKeyframeIndex >= 0 || SelectedEventIndex >= 0;

        /// <summary>Total number of track lanes including event lanes.</summary>
        public int TotalLaneCount
        {
            get
            {
                int count = Tracks?.Count ?? 0;
                if (IsCutscene && AudioEvents != null) count++; // audio event lane
                if (SkinAnimEvents != null && SkinAnimEvents.Count > 0) count++; // skin anim lane
                return count;
            }
        }

        /// <summary>Total height of all track and event lanes.</summary>
        public float TotalTracksHeight
        {
            get
            {
                float h = (Tracks?.Count ?? 0) * TrackHeight;
                if (IsCutscene) h += EventLaneHeight; // audio events
                if (SkinAnimEvents != null && SkinAnimEvents.Count > 0) h += EventLaneHeight;
                h += TrackHeight; // "add track" row
                return h;
            }
        }
    }
}
#endif
