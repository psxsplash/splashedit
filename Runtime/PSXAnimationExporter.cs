using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{
    /// <summary>
    /// Serializes PSXAnimationClip data into the splashpack v17 binary format.
    /// Animations have no camera tracks and no audio events.
    /// </summary>
    public static class PSXAnimationExporter
    {
        private const int MAX_ANIMATIONS = 16;
        private const int MAX_TRACKS = 8;
        private const int MAX_KEYFRAMES = 64;
        private const int MAX_SKIN_ANIM_EVENTS = 16;
        private const int MAX_NAME_LEN = 24;

        private static short DegreesToAngleRaw(float degrees)
        {
            float raw = degrees * 1024.0f / 180.0f;
            return (short)Mathf.Clamp(Mathf.RoundToInt(raw), -32768, 32767);
        }

        public static void ExportAnimations(
            BinaryWriter writer,
            PSXAnimationClip[] animations,
            PSXObjectExporter[] exporters,
            PSXSkinnedObjectExporter[] skinnedExporters,
            float gteScaling,
            out long animationTableStart,
            Action<string, LogType> log = null)
        {
            animationTableStart = 0;

            if (animations == null || animations.Length == 0)
                return;

            if (animations.Length > MAX_ANIMATIONS)
            {
                log?.Invoke($"Too many animations ({animations.Length} > {MAX_ANIMATIONS}). Only the first {MAX_ANIMATIONS} will be exported.", LogType.Warning);
                var trimmed = new PSXAnimationClip[MAX_ANIMATIONS];
                Array.Copy(animations, trimmed, MAX_ANIMATIONS);
                animations = trimmed;
            }

            AlignToFourBytes(writer);

            // Animation Table
            animationTableStart = writer.BaseStream.Position;

            // SPLASHPACKAnimationEntry: 12 bytes each (same layout as cutscene entry)
            long[] entryPositions = new long[animations.Length];
            for (int i = 0; i < animations.Length; i++)
            {
                entryPositions[i] = writer.BaseStream.Position;
                writer.Write((uint)0);  // dataOffset placeholder
                writer.Write((byte)0);  // nameLen placeholder
                writer.Write((byte)0);  // pad
                writer.Write((byte)0);  // pad
                writer.Write((byte)0);  // pad
                writer.Write((uint)0);  // nameOffset placeholder
            }

            // Per-animation data
            for (int ai = 0; ai < animations.Length; ai++)
            {
                PSXAnimationClip clip = animations[ai];
                AlignToFourBytes(writer);

                long dataPos = writer.BaseStream.Position;

                // Filter out camera tracks
                var validTracks = new List<PSXCutsceneTrack>();
                if (clip.Tracks != null)
                {
                    foreach (var track in clip.Tracks)
                    {
                        if (track.TrackType == PSXTrackType.CameraPosition ||
                            track.TrackType == PSXTrackType.CameraRotation)
                        {
                            log?.Invoke($"Animation '{clip.AnimationName}': camera track type '{track.TrackType}' not allowed in animations. Skipping.", LogType.Warning);
                            continue;
                        }
                        validTracks.Add(track);
                        if (validTracks.Count >= MAX_TRACKS) break;
                    }
                }
                int trackCount = validTracks.Count;

                // Validate and collect skin anim events
                List<PSXSkinAnimEvent> validSkinAnims = new List<PSXSkinAnimEvent>();
                if (clip.SkinAnimEvents != null && skinnedExporters != null)
                {
                    foreach (var evt in clip.SkinAnimEvents)
                    {
                        bool found = false;
                        foreach (var skinExp in skinnedExporters)
                        {
                            if (skinExp.gameObject.name == evt.TargetObjectName)
                            {
                                bool clipFound = false;
                                for (int c = 0; c < skinExp.AnimationClips.Length; c++)
                                {
                                    if (skinExp.AnimationClips[c] != null && skinExp.AnimationClips[c].name == evt.ClipName)
                                    { clipFound = true; break; }
                                }
                                if (clipFound) { found = true; break; }
                                else
                                    log?.Invoke($"Animation '{clip.AnimationName}': skin anim clip '{evt.ClipName}' not found on '{evt.TargetObjectName}'. Skipping.", LogType.Warning);
                            }
                        }
                        if (found) validSkinAnims.Add(evt);
                        else
                            log?.Invoke($"Animation '{clip.AnimationName}': skinned object '{evt.TargetObjectName}' not found. Skipping skin anim event.", LogType.Warning);
                        if (validSkinAnims.Count >= MAX_SKIN_ANIM_EVENTS) break;
                    }
                }
                validSkinAnims.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                int skinAnimEventCount = validSkinAnims.Count;

                // SPLASHPACKAnimation: 16 bytes (v19: added skin anim events)
                long tracksOffsetPlaceholder;
                long skinAnimEventsOffsetPlaceholder;

                writer.Write((ushort)clip.DurationFrames);
                writer.Write((byte)trackCount);
                writer.Write((byte)0);  // pad
                tracksOffsetPlaceholder = writer.BaseStream.Position;
                writer.Write((uint)0);  // tracksOffset placeholder
                // v19 extension: skin anim events
                writer.Write((byte)skinAnimEventCount);
                writer.Write((byte)0);  // pad
                writer.Write((byte)0);  // pad
                writer.Write((byte)0);  // pad
                skinAnimEventsOffsetPlaceholder = writer.BaseStream.Position;
                writer.Write((uint)0);  // skinAnimEventsOffset placeholder

                // Tracks
                AlignToFourBytes(writer);
                long tracksStart = writer.BaseStream.Position;

                long[] trackObjectNameOffsets = new long[trackCount];
                long[] trackKeyframesOffsets = new long[trackCount];

                for (int ti = 0; ti < trackCount; ti++)
                {
                    PSXCutsceneTrack track = validTracks[ti];
                    string objName = GetTrackTargetName(track);

                    int kfCount = Mathf.Min(track.Keyframes?.Count ?? 0, MAX_KEYFRAMES);

                    writer.Write((byte)track.TrackType);
                    writer.Write((byte)kfCount);
                    writer.Write((byte)objName.Length);
                    writer.Write((byte)0);  // pad
                    trackObjectNameOffsets[ti] = writer.BaseStream.Position;
                    writer.Write((uint)0);  // objectNameOffset placeholder
                    trackKeyframesOffsets[ti] = writer.BaseStream.Position;
                    writer.Write((uint)0);  // keyframesOffset placeholder
                }

                // Keyframe data per track
                for (int ti = 0; ti < trackCount; ti++)
                {
                    PSXCutsceneTrack track = validTracks[ti];
                    int kfCount = Mathf.Min(track.Keyframes?.Count ?? 0, MAX_KEYFRAMES);

                    AlignToFourBytes(writer);
                    long kfStart = writer.BaseStream.Position;

                    var sortedKf = new List<PSXKeyframe>(track.Keyframes ?? new List<PSXKeyframe>());
                    sortedKf.Sort((a, b) => a.Frame.CompareTo(b.Frame));

                    for (int ki = 0; ki < kfCount; ki++)
                    {
                        PSXKeyframe kf = sortedKf[ki];
                        ushort frameAndInterp = (ushort)((((int)kf.Interp & 0x7) << 13) | (kf.Frame & 0x1FFF));
                        writer.Write(frameAndInterp);

                        switch (track.TrackType)
                        {
                            case PSXTrackType.ObjectPosition:
                            {
                                float gte = gteScaling;
                                short px = PSXTrig.ConvertCoordinateToPSX(kf.Value.x, gte);
                                short py = PSXTrig.ConvertCoordinateToPSX(-kf.Value.y, gte);
                                short pz = PSXTrig.ConvertCoordinateToPSX(kf.Value.z, gte);
                                writer.Write(px);
                                writer.Write(py);
                                writer.Write(pz);
                                break;
                            }
                            case PSXTrackType.ObjectRotation:
                            {
                                // Negate X (pitch) and Z (roll) to compensate for the
                                // Y-axis flip between Unity (Y-up) and PSX (Y-down).
                                short rx = DegreesToAngleRaw(-kf.Value.x);
                                short ry = DegreesToAngleRaw(kf.Value.y);
                                short rz = DegreesToAngleRaw(-kf.Value.z);
                                writer.Write(rx);
                                writer.Write(ry);
                                writer.Write(rz);
                                break;
                            }
                            case PSXTrackType.ObjectActive:
                            {
                                writer.Write((short)(kf.Value.x > 0.5f ? 1 : 0));
                                writer.Write((short)0);
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.ObjectUVOffset:
                            {
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 0, 255));
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.y), 0, 255));
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.UICanvasVisible:
                            case PSXTrackType.UIElementVisible:
                            {
                                writer.Write((short)(kf.Value.x > 0.5f ? 1 : 0));
                                writer.Write((short)0);
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.UIProgress:
                            {
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 0, 100));
                                writer.Write((short)0);
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.UIPosition:
                            {
                                writer.Write((short)Mathf.RoundToInt(kf.Value.x));
                                writer.Write((short)Mathf.RoundToInt(kf.Value.y));
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.UIColor:
                            {
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 0, 255));
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.y), 0, 255));
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.z), 0, 255));
                                break;
                            }
                            case PSXTrackType.RumbleSmall:
                            {
                                // Step: values[0] = 0 (off) or 1 (on)
                                writer.Write((short)(kf.Value.x > 0.5f ? 1 : 0));
                                writer.Write((short)0);
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.RumbleLarge:
                            {
                                // values[0] = motor speed 0-255
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 0, 255));
                                writer.Write((short)0);
                                writer.Write((short)0);
                                break;
                            }
                        }
                    }

                    // Backfill keyframes offset
                    {
                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)trackKeyframesOffsets[ti], SeekOrigin.Begin);
                        writer.Write((uint)kfStart);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }
                }

                // Object / UI target name strings
                for (int ti = 0; ti < trackCount; ti++)
                {
                    PSXCutsceneTrack track = validTracks[ti];
                    string objName = GetTrackTargetName(track);

                    if (objName.Length > 0)
                    {
                        long namePos = writer.BaseStream.Position;
                        byte[] nameBytes = Encoding.UTF8.GetBytes(objName);
                        writer.Write(nameBytes);
                        writer.Write((byte)0);

                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)trackObjectNameOffsets[ti], SeekOrigin.Begin);
                        writer.Write((uint)namePos);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }
                }

                // ── Skin anim events (v19) ──
                AlignToFourBytes(writer);
                long skinAnimEventsStart = writer.BaseStream.Position;

                foreach (var evt in validSkinAnims)
                {
                    byte meshIdx = 0;
                    byte clipIdx2 = 0;
                    if (skinnedExporters != null)
                    {
                        for (int si = 0; si < skinnedExporters.Length; si++)
                        {
                            if (skinnedExporters[si].gameObject.name == evt.TargetObjectName)
                            {
                                meshIdx = (byte)si;
                                for (int c = 0; c < skinnedExporters[si].AnimationClips.Length; c++)
                                {
                                    if (skinnedExporters[si].AnimationClips[c] != null &&
                                        skinnedExporters[si].AnimationClips[c].name == evt.ClipName)
                                    { clipIdx2 = (byte)c; break; }
                                }
                                break;
                            }
                        }
                    }
                    writer.Write((ushort)evt.Frame);
                    writer.Write(meshIdx);
                    writer.Write(clipIdx2);
                    writer.Write((byte)(evt.Loop ? 1 : 0));
                    writer.Write((byte)0); // pad
                    writer.Write((byte)0); // pad
                    writer.Write((byte)0); // pad
                }

                // Animation name string
                string anName = clip.AnimationName ?? "unnamed";
                if (anName.Length > MAX_NAME_LEN) anName = anName.Substring(0, MAX_NAME_LEN);
                long nameStartPos = writer.BaseStream.Position;
                byte[] anNameBytes = Encoding.UTF8.GetBytes(anName);
                writer.Write(anNameBytes);
                writer.Write((byte)0);

                // Backfill tracksOffset
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)tracksOffsetPlaceholder, SeekOrigin.Begin);
                    writer.Write((uint)tracksStart);
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }
                // Backfill skinAnimEventsOffset (v19)
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)skinAnimEventsOffsetPlaceholder, SeekOrigin.Begin);
                    writer.Write((uint)(skinAnimEventCount > 0 ? skinAnimEventsStart : 0));
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }

                // Backfill animation table entry
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)entryPositions[ai], SeekOrigin.Begin);
                    writer.Write((uint)dataPos);
                    writer.Write((byte)anNameBytes.Length);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((uint)nameStartPos);
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }
            }

            log?.Invoke($"{animations.Length} animation(s) exported.", LogType.Log);
        }

        private static void AlignToFourBytes(BinaryWriter writer)
        {
            long pos = writer.BaseStream.Position;
            int padding = (int)(4 - (pos % 4)) % 4;
            if (padding > 0)
                writer.Write(new byte[padding]);
        }

        private static string GetTrackTargetName(PSXCutsceneTrack track)
        {
            // Vibration tracks are global (no target object)
            if (track.IsVibrationTrack) return "";

            string name;
            if (track.IsUIElementTrack)
                name = (track.UICanvasName ?? "") + "/" + (track.UIElementName ?? "");
            else if (track.IsUITrack)
                name = track.UICanvasName ?? "";
            else
                name = track.ObjectName ?? "";

            if (name.Length > MAX_NAME_LEN)
                name = name.Substring(0, MAX_NAME_LEN);
            return name;
        }
    }
}
