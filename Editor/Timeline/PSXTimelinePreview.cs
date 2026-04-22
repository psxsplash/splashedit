#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SplashEdit.RuntimeCode;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Unified preview system for the PSX Timeline Editor.
    /// Extracted and merged from PSXAnimationEditor and PSXCutsceneEditor.
    /// Handles object transform save/restore, camera driving (cutscene),
    /// skinned mesh animation sampling, and audio preview.
    /// </summary>
    public class PSXTimelinePreview
    {
        // Saved scene-view state (cutscene only)
        private bool _hasSavedSceneView;
        private Vector3 _savedPivot;
        private Quaternion _savedRotation;
        private float _savedSize;
        private float _savedFOV;

        // Saved object transforms
        private Dictionary<string, Vector3> _savedObjectPositions = new Dictionary<string, Vector3>();
        private Dictionary<string, Quaternion> _savedObjectRotations = new Dictionary<string, Quaternion>();
        private Dictionary<string, bool> _savedObjectActive = new Dictionary<string, bool>();
        private Dictionary<string, Vector2> _savedObjectUVOffset = new Dictionary<string, Vector2>();

        // Audio preview
        private Dictionary<string, AudioClip> _audioClipCache = new Dictionary<string, AudioClip>();
        private HashSet<int> _firedAudioEventIndices = new HashSet<int>();

        // Skinned mesh animation
        private bool _animModeStarted;
        private Dictionary<string, Animator> _skinAnimatorCache = new Dictionary<string, Animator>();

        // PS1 H register / Unity FOV conversion
        private const float PSX_HALF_HEIGHT = 120f;
        private static float HToFOV(float h)
        {
            if (h <= 0f) h = 1f;
            return 2f * Mathf.Atan(PSX_HALF_HEIGHT / h) * Mathf.Rad2Deg;
        }

        // =====================================================================
        // Start / Stop
        // =====================================================================

        public void StartPreview(PSXTimelineState state)
        {
            _firedAudioEventIndices.Clear();
            _savedObjectPositions.Clear();
            _savedObjectRotations.Clear();
            _savedObjectActive.Clear();
            _savedObjectUVOffset.Clear();
            _hasSavedSceneView = false;

            // Save scene view camera (cutscene only)
            if (state.IsCutscene)
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv != null)
                {
                    _hasSavedSceneView = true;
                    _savedPivot = sv.pivot;
                    _savedRotation = sv.rotation;
                    _savedSize = sv.size;
                    _savedFOV = sv.cameraSettings.fieldOfView;
                }
            }

            // Save object transforms
            var tracks = state.Tracks;
            if (tracks != null)
            {
                foreach (var track in tracks)
                {
                    if (track.IsCameraTrack || track.IsUITrack) continue;
                    if (string.IsNullOrEmpty(track.ObjectName)) continue;

                    var go = GameObject.Find(track.ObjectName);
                    if (go == null) continue;

                    if (!_savedObjectPositions.ContainsKey(track.ObjectName))
                    {
                        _savedObjectPositions[track.ObjectName] = go.transform.position;
                        _savedObjectRotations[track.ObjectName] = go.transform.rotation;
                        _savedObjectActive[track.ObjectName] = go.activeSelf;
                        if (go.GetComponent<MeshRenderer>() && go.GetComponent<PSXObjectExporter>())
                        {
                            int offsetMaterial = go.GetComponent<PSXObjectExporter>().UVOffsetMaterial;
                            // Accessing them this way stops an error, but doesn't seem to be an issue elsewhere...
                            List<Material> mats = new List<Material>();
                            go.GetComponent<MeshRenderer>().GetMaterials(mats);
                            _savedObjectUVOffset[track.ObjectName] = mats[offsetMaterial].mainTextureOffset;
                        }
                    }
                }
            }

            // Audio clip lookup (cutscene only)
            _audioClipCache.Clear();
            if (state.IsCutscene)
            {
                var audioSources = Object.FindObjectsByType<PSXAudioClip>(FindObjectsSortMode.None);
                foreach (var a in audioSources)
                    if (!string.IsNullOrEmpty(a.ClipName) && a.Clip != null)
                        _audioClipCache[a.ClipName] = a.Clip;
            }

            // Prepare skinned mesh anim preview
            _animModeStarted = false;
            _skinAnimatorCache.Clear();
            var skinEvents = state.SkinAnimEvents;
            if (skinEvents != null && skinEvents.Count > 0)
            {
                var skinTargetNames = new HashSet<string>();
                foreach (var evt in skinEvents)
                    if (!string.IsNullOrEmpty(evt.TargetObjectName))
                        skinTargetNames.Add(evt.TargetObjectName);

                bool needsAnimMode = false;
                var skinnedExporters = Object.FindObjectsByType<PSXSkinnedObjectExporter>(FindObjectsSortMode.None);
                foreach (var se in skinnedExporters)
                {
                    string objName = se.gameObject.name;

                    if (skinTargetNames.Contains(objName) && !_savedObjectPositions.ContainsKey(objName))
                    {
                        _savedObjectPositions[objName] = se.transform.position;
                        _savedObjectRotations[objName] = se.transform.rotation;
                        _savedObjectActive[objName] = se.gameObject.activeSelf;
                        if (se.GetComponent<SkinnedMeshRenderer>() && se.GetComponent<PSXObjectExporter>())
                        {
                            int offsetMaterial = se.GetComponent<PSXObjectExporter>().UVOffsetMaterial;
                            _savedObjectUVOffset[objName] = se.GetComponent<SkinnedMeshRenderer>().materials[offsetMaterial].mainTextureOffset;
                        }
                    }
                    if (_skinAnimatorCache.ContainsKey(objName)) continue;

                    Animator resolved = ResolveAnimatorForSkinExp(se);
                    _skinAnimatorCache[objName] = resolved;
                    if (resolved != null) needsAnimMode = true;
                }

                if (needsAnimMode && !AnimationMode.InAnimationMode())
                {
                    AnimationMode.StartAnimationMode();
                    _animModeStarted = true;
                }
            }

            state.IsPreviewing = true;
        }

        public void StopPreview(PSXTimelineState state)
        {
            state.IsPreviewing = false;
            state.IsPlaying = false;

            // Restore scene view camera
            if (_hasSavedSceneView)
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv != null)
                {
                    sv.pivot = _savedPivot;
                    sv.rotation = _savedRotation;
                    sv.size = _savedSize;
                    sv.cameraSettings.fieldOfView = _savedFOV;
                    sv.Repaint();
                }
                _hasSavedSceneView = false;
            }

            // Restore object transforms
            foreach (var kvp in _savedObjectPositions)
            {
                var go = GameObject.Find(kvp.Key);
                if (go == null) continue;
                go.transform.position = kvp.Value;
                if (_savedObjectRotations.ContainsKey(kvp.Key))
                    go.transform.rotation = _savedObjectRotations[kvp.Key];
                if (_savedObjectActive.ContainsKey(kvp.Key))
                    go.SetActive(_savedObjectActive[kvp.Key]);
                if (_savedObjectUVOffset.ContainsKey(kvp.Key))
                {
                    if (go.GetComponent<MeshRenderer>())
                    {
                        int offsetMaterial = go.GetComponent<PSXObjectExporter>().UVOffsetMaterial;
                        go.GetComponent<MeshRenderer>().materials[offsetMaterial].mainTextureOffset = _savedObjectUVOffset[kvp.Key];
                    }
                    else if (go.GetComponent<SkinnedMeshRenderer>())
                    {
                        int offsetMaterial = go.GetComponent<PSXObjectExporter>().UVOffsetMaterial;
                        go.GetComponent<SkinnedMeshRenderer>().materials[offsetMaterial].mainTextureOffset = _savedObjectUVOffset[kvp.Key];
                    }
                }
            }
            _savedObjectPositions.Clear();
            _savedObjectRotations.Clear();
            _savedObjectActive.Clear();
            _savedObjectUVOffset.Clear();

            // Restore skinned mesh poses
            if (_animModeStarted && AnimationMode.InAnimationMode())
            {
                AnimationMode.StopAnimationMode();
                _animModeStarted = false;
            }

            SceneView.RepaintAll();
        }

        // =====================================================================
        // Apply Preview at Current Frame
        // =====================================================================

        public void ApplyPreview(PSXTimelineState state)
        {
            if (!state.IsPreviewing) return;
            float frame = state.PlayheadFrame;

            var sv = SceneView.lastActiveSceneView;
            Vector3? camPos = null;
            Quaternion? camRot = null;
            float? camH = null;

            var tracks = state.Tracks;
            if (tracks != null)
            {
                foreach (var track in tracks)
                {
                    Vector3 initialVal = GetInitialValue(track);
                    Vector3 val = EvaluateTrack(track, frame, initialVal);

                    switch (track.TrackType)
                    {
                        case PSXTrackType.CameraPosition:
                            camPos = val;
                            break;
                        case PSXTrackType.CameraRotation:
                            camRot = Quaternion.Euler(val);
                            break;
                        case PSXTrackType.CameraH:
                            camH = val.x;
                            break;
                        case PSXTrackType.ObjectPosition:
                        {
                            var go = GameObject.Find(track.ObjectName);
                            if (go != null) go.transform.position = val;
                            break;
                        }
                        case PSXTrackType.ObjectRotation:
                        {
                            var go = GameObject.Find(track.ObjectName);
                            if (go != null) go.transform.rotation = Quaternion.Euler(val);
                            break;
                        }
                        case PSXTrackType.ObjectActive:
                        {
                            var go = GameObject.Find(track.ObjectName);
                            if (go != null) go.SetActive(val.x > 0.5f);
                            break;
                        }
                        case PSXTrackType.ObjectUVOffset:
                        {
                            var go = GameObject.Find(track.ObjectName);
                            if (go != null)
                            {
                                if (go.GetComponent<MeshRenderer>())
                                {
                                    int offsetMaterial = go.GetComponent<PSXObjectExporter>().UVOffsetMaterial;
                                    go.GetComponent<MeshRenderer>().materials[offsetMaterial].mainTextureOffset = val / 256;
                                }
                                else if (go.GetComponent<SkinnedMeshRenderer>())
                                {
                                    int offsetMaterial = go.GetComponent<PSXObjectExporter>().UVOffsetMaterial;
                                    go.GetComponent<SkinnedMeshRenderer>().materials[offsetMaterial].mainTextureOffset = val / 256;
                                }
                            }
                            break;
                        }
                        // UI tracks: no scene preview
                    }
                }
            }

            // Drive scene view camera (cutscene only)
            if (state.IsCutscene && sv != null && (camPos.HasValue || camRot.HasValue || camH.HasValue))
            {
                Vector3 pos = camPos ?? sv.camera.transform.position;
                Quaternion rot = camRot ?? sv.camera.transform.rotation;

                sv.rotation = rot;
                sv.pivot = pos + rot * Vector3.forward * sv.cameraDistance;

                if (camH.HasValue)
                {
                    float h = Mathf.Clamp(camH.Value, 1f, 1024f);
                    sv.cameraSettings.fieldOfView = HToFOV(h);
                }
                sv.Repaint();
            }

            // Fire audio events (cutscene, only during playback)
            if (state.IsPlaying && state.IsCutscene)
            {
                var audioEvents = state.AudioEvents;
                if (audioEvents != null)
                {
                    for (int i = 0; i < audioEvents.Count; i++)
                    {
                        if (_firedAudioEventIndices.Contains(i)) continue;
                        if (frame >= audioEvents[i].Frame)
                        {
                            _firedAudioEventIndices.Add(i);
                            PlayAudioPreview(audioEvents[i]);
                        }
                    }
                }
            }

            // Apply skin anim events
            ApplySkinAnimPreview(state, frame);

            SceneView.RepaintAll();
        }

        public void ResetAudioEvents()
        {
            _firedAudioEventIndices.Clear();
        }

        /// <summary>
        /// Mark all audio events at or before the current playhead as already fired,
        /// so they don't retroactively trigger when starting playback mid-timeline.
        /// </summary>
        public void SeedFiredEventsUpTo(PSXTimelineState state)
        {
            _firedAudioEventIndices.Clear();
            var audioEvents = state.AudioEvents;
            if (audioEvents == null) return;
            for (int i = 0; i < audioEvents.Count; i++)
            {
                if (audioEvents[i].Frame <= (int)state.PlayheadFrame)
                    _firedAudioEventIndices.Add(i);
            }
        }

        // =====================================================================
        // Initial Values (for pre-first-keyframe blending)
        // =====================================================================

        private Vector3 GetInitialValue(PSXCutsceneTrack track)
        {
            switch (track.TrackType)
            {
                case PSXTrackType.CameraPosition:
                    if (_hasSavedSceneView)
                        return _savedPivot - _savedRotation * Vector3.forward * _savedSize;
                    return Vector3.zero;
                case PSXTrackType.CameraRotation:
                    return _hasSavedSceneView ? _savedRotation.eulerAngles : Vector3.zero;
                case PSXTrackType.CameraH:
                    return _hasSavedSceneView ? new Vector3(PSX_HALF_HEIGHT / Mathf.Tan(_savedFOV * 0.5f * Mathf.Deg2Rad), 0, 0) : new Vector3(120, 0, 0);
                case PSXTrackType.ObjectPosition:
                    if (_savedObjectPositions.TryGetValue(track.ObjectName ?? "", out var pos)) return pos;
                    return Vector3.zero;
                case PSXTrackType.ObjectRotation:
                    if (_savedObjectRotations.TryGetValue(track.ObjectName ?? "", out var rot)) return rot.eulerAngles;
                    return Vector3.zero;
                case PSXTrackType.ObjectActive:
                    if (_savedObjectActive.TryGetValue(track.ObjectName ?? "", out var active))
                        return new Vector3(active ? 1f : 0f, 0, 0);
                    return new Vector3(1f, 0, 0);
                case PSXTrackType.UICanvasVisible:
                case PSXTrackType.UIElementVisible:
                    return new Vector3(1f, 0, 0);
                default:
                    return Vector3.zero;
            }
        }

        // =====================================================================
        // Track Evaluation (matches C++ runtime)
        // =====================================================================

        public static Vector3 EvaluateTrack(PSXCutsceneTrack track, float frame, Vector3 initialValue)
        {
            if (track.Keyframes == null || track.Keyframes.Count == 0)
                return initialValue;

            // Step interpolation tracks
            if (track.TrackType == PSXTrackType.ObjectActive ||
                track.TrackType == PSXTrackType.UICanvasVisible ||
                track.TrackType == PSXTrackType.UIElementVisible)
            {
                if (track.Keyframes.Count > 0 && track.Keyframes[0].Frame > 0 && frame < track.Keyframes[0].Frame)
                    return initialValue;
                return EvaluateStep(track.Keyframes, frame);
            }

            // Find surrounding keyframes
            PSXKeyframe before = null, after = null;
            for (int i = 0; i < track.Keyframes.Count; i++)
            {
                if (track.Keyframes[i].Frame <= frame)
                    before = track.Keyframes[i];
                if (track.Keyframes[i].Frame >= frame && after == null)
                    after = track.Keyframes[i];
            }

            if (before == null && after == null) return Vector3.zero;

            // Pre-first-keyframe: blend from initial value
            if (before == null && after != null && after.Frame > 0 && frame < after.Frame)
            {
                float rawT = frame / after.Frame;
                float t = ApplyInterpCurve(rawT, after.Interp);
                return Vector3.Lerp(initialValue, after.Value, t);
            }

            if (before == null) return after.Value;
            if (after == null) return before.Value;
            if (before == after) return before.Value;

            float span = after.Frame - before.Frame;
            float rawT2 = (frame - before.Frame) / span;
            float t2 = ApplyInterpCurve(rawT2, after.Interp);

            return Vector3.Lerp(before.Value, after.Value, t2);
        }

        public static float ApplyInterpCurve(float t, PSXInterpMode mode)
        {
            switch (mode)
            {
                default:
                case PSXInterpMode.Linear:
                    return t;
                case PSXInterpMode.Step:
                    return 0f;
                case PSXInterpMode.EaseIn:
                    return t * t;
                case PSXInterpMode.EaseOut:
                    return t * (2f - t);
                case PSXInterpMode.EaseInOut:
                    return t * t * (3f - 2f * t);
            }
        }

        private static Vector3 EvaluateStep(List<PSXKeyframe> keyframes, float frame)
        {
            Vector3 result = Vector3.zero;
            for (int i = 0; i < keyframes.Count; i++)
            {
                if (keyframes[i].Frame <= frame)
                    result = keyframes[i].Value;
            }
            return result;
        }

        // =====================================================================
        // Skin Animation Preview
        // =====================================================================

        private void ApplySkinAnimPreview(PSXTimelineState state, float frame)
        {
            var skinEvents = state.SkinAnimEvents;
            if (skinEvents == null || skinEvents.Count == 0) return;

            var skinnedExporters = Object.FindObjectsByType<PSXSkinnedObjectExporter>(FindObjectsSortMode.None);
            var skinExpByName = new Dictionary<string, PSXSkinnedObjectExporter>();
            foreach (var se in skinnedExporters)
                if (!skinExpByName.ContainsKey(se.gameObject.name))
                    skinExpByName[se.gameObject.name] = se;

            // For each target, find the LAST triggered event (highest frame <= current)
            var activeSkinEvents = new Dictionary<string, PSXSkinAnimEvent>();
            foreach (var evt in skinEvents)
            {
                if (string.IsNullOrEmpty(evt.TargetObjectName)) continue;
                if (evt.Frame > (int)frame) continue;
                activeSkinEvents[evt.TargetObjectName] = evt;
            }

            bool didBeginSampling = false;
            foreach (var kvp in activeSkinEvents)
            {
                var evt = kvp.Value;
                if (!skinExpByName.TryGetValue(evt.TargetObjectName, out var skinExp)) continue;
                if (skinExp.AnimationClips == null) continue;

                AnimationClip animClip = null;
                foreach (var ac in skinExp.AnimationClips)
                {
                    if (ac != null && ac.name == evt.ClipName)
                    {
                        animClip = ac;
                        break;
                    }
                }
                if (animClip == null) continue;

                float elapsedSec = (frame - evt.Frame) / 30f;
                if (evt.Loop && animClip.length > 0f)
                    elapsedSec = elapsedSec % animClip.length;
                else
                    elapsedSec = Mathf.Min(elapsedSec, animClip.length);

                bool isLegacy = true;
                if (_skinAnimatorCache.TryGetValue(evt.TargetObjectName, out var cachedAnim))
                    isLegacy = (cachedAnim == null);

                var savedPos = skinExp.transform.localPosition;
                var savedRot = skinExp.transform.localRotation;

                if (isLegacy)
                {
                    bool wasLegacy = animClip.legacy;
                    animClip.legacy = true;
                    animClip.SampleAnimation(skinExp.gameObject, elapsedSec);
                    animClip.legacy = wasLegacy;

                    skinExp.transform.localPosition = savedPos;
                    skinExp.transform.localRotation = savedRot;
                }
                else
                {
                    if (_animModeStarted && AnimationMode.InAnimationMode())
                    {
                        if (cachedAnim != null)
                        {
                            var savedAnimPos = cachedAnim.transform.localPosition;
                            var savedAnimRot = cachedAnim.transform.localRotation;

                            if (!didBeginSampling) { AnimationMode.BeginSampling(); didBeginSampling = true; }
                            AnimationMode.SampleAnimationClip(cachedAnim.gameObject, animClip, elapsedSec);

                            skinExp.transform.localPosition = savedPos;
                            skinExp.transform.localRotation = savedRot;
                            cachedAnim.transform.localPosition = savedAnimPos;
                            cachedAnim.transform.localRotation = savedAnimRot;
                        }
                    }
                }
            }
            if (didBeginSampling) AnimationMode.EndSampling();
        }

        // =====================================================================
        // Resolve Animator for Skinned Object (static utility)
        // =====================================================================

        public static Animator ResolveAnimatorForSkinExp(PSXSkinnedObjectExporter skinExp)
        {
            var smr = skinExp.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null || smr.sharedMesh == null) return null;

            Avatar modelAvatar = null;
            string meshAssetPath = AssetDatabase.GetAssetPath(smr.sharedMesh);
            if (!string.IsNullOrEmpty(meshAssetPath))
            {
                foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(meshAssetPath))
                {
                    if (sub is Avatar a) { modelAvatar = a; break; }
                }
            }

            if (modelAvatar == null) return null;

            Animator animator = skinExp.GetComponentInChildren<Animator>();

            Transform boneHierarchyRoot = skinExp.transform;
            if (smr.rootBone != null && modelAvatar.isHuman)
            {
                Transform candidate = smr.rootBone.parent;
                while (candidate != null)
                {
                    Animator existingAnim = candidate.GetComponent<Animator>();
                    if (existingAnim != null)
                    {
                        var savedAvatar = existingAnim.avatar;
                        var probePos = existingAnim.transform.localPosition;
                        var probeRot = existingAnim.transform.localRotation;
                        existingAnim.avatar = modelAvatar;
                        existingAnim.Rebind();
                        bool ok = existingAnim.GetBoneTransform(HumanBodyBones.Hips) != null;
                        existingAnim.avatar = savedAvatar;
                        existingAnim.Rebind();
                        existingAnim.transform.localPosition = probePos;
                        existingAnim.transform.localRotation = probeRot;
                        if (ok) { boneHierarchyRoot = candidate; break; }
                    }
                    candidate = candidate.parent;
                }
            }
            else if (smr.rootBone != null)
            {
                Transform t = smr.rootBone;
                while (t.parent != null)
                {
                    t = t.parent;
                    if (smr.transform.IsChildOf(t)) break;
                }
                boneHierarchyRoot = t;
            }

            if (animator == null || !animator.transform.IsChildOf(boneHierarchyRoot))
                animator = boneHierarchyRoot.GetComponentInChildren<Animator>();
            if (animator == null)
                animator = boneHierarchyRoot.GetComponent<Animator>();
            if (animator == null)
                animator = boneHierarchyRoot.gameObject.AddComponent<Animator>();

            if (animator.avatar == null && modelAvatar != null)
                animator.avatar = modelAvatar;

            // Save transforms before Rebind to prevent teleportation
            var saveExpPos = skinExp.transform.localPosition;
            var saveExpRot = skinExp.transform.localRotation;
            var saveAnimPos = animator.transform.localPosition;
            var saveAnimRot = animator.transform.localRotation;
            var smrR = skinExp.GetComponentInChildren<SkinnedMeshRenderer>();
            var savedInterR = new List<(Transform t, Vector3 p, Quaternion r)>();
            if (smrR != null && smrR.rootBone != null && smrR.rootBone != skinExp.transform)
            {
                for (Transform w = smrR.rootBone; w != null && w != skinExp.transform; w = w.parent)
                    savedInterR.Add((w, w.localPosition, w.localRotation));
            }
            animator.Rebind();
            skinExp.transform.localPosition = saveExpPos;
            skinExp.transform.localRotation = saveExpRot;
            animator.transform.localPosition = saveAnimPos;
            animator.transform.localRotation = saveAnimRot;
            foreach (var (t, p, r) in savedInterR)
            { t.localPosition = p; t.localRotation = r; }
            return animator;
        }

        // =====================================================================
        // Audio Preview
        // =====================================================================

        private void PlayAudioPreview(PSXAudioEvent evt)
        {
            if (string.IsNullOrEmpty(evt.ClipName)) return;
            if (!_audioClipCache.TryGetValue(evt.ClipName, out AudioClip clip)) return;

            var unityEditorAssembly = typeof(AudioImporter).Assembly;
            var audioUtilClass = unityEditorAssembly.GetType("UnityEditor.AudioUtil");
            if (audioUtilClass == null) return;

            var stopMethod = audioUtilClass.GetMethod("StopAllPreviewClips",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            stopMethod?.Invoke(null, null);

            var playMethod = audioUtilClass.GetMethod("PlayPreviewClip",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                null, new System.Type[] { typeof(AudioClip), typeof(int), typeof(bool) }, null);
            playMethod?.Invoke(null, new object[] { clip, 0, false });
        }
    }
}
#endif
