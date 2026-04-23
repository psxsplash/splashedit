using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{
    /// <summary>
    /// Serializes PSXCutsceneClip data into the splashpack v12 binary format.
    /// Called from PSXSceneWriter.Write() after all other data sections.
    /// </summary>
    public static class PSXCutsceneExporter
    {
        // Match C++ limits
        private const int MAX_CUTSCENES = 16;
        private const int MAX_TRACKS = 8;
        private const int MAX_KEYFRAMES = 64;
        private const int MAX_AUDIO_EVENTS = 64;
        private const int MAX_SKIN_ANIM_EVENTS = 16;
        private const int MAX_NAME_LEN = 24;

        /// <summary>
        /// Angle conversion: degrees to psyqo::Angle raw value.
        /// psyqo::Angle = FixedPoint&lt;10&gt;, stored in pi-units.
        /// 1.0_pi = 1024 raw = 180 degrees. So: raw = degrees * 1024 / 180.
        /// </summary>
        private static short DegreesToAngleRaw(float degrees)
        {
            float raw = degrees * 1024.0f / 180.0f;
            return (short)Mathf.Clamp(Mathf.RoundToInt(raw), -32768, 32767);
        }

        /// <summary>
        /// Write all cutscene data and return the byte position of the cutscene table
        /// so the header can be backfilled.
        /// </summary>
        /// <param name="writer">Binary writer positioned after all prior sections.</param>
        /// <param name="cutscenes">Cutscene clips to export (may be null/empty).</param>
        /// <param name="exporters">Scene object exporters for name validation.</param>
        /// <param name="audioSources">Audio sources for clip name → index resolution.</param>
        /// <param name="gteScaling">GTE scaling factor.</param>
        /// <param name="cutsceneTableStart">Returns the file position where the cutscene table starts.</param>
        /// <param name="log">Optional log callback.</param>
        public static void ExportCutscenes(
            BinaryWriter writer,
            PSXCutsceneClip[] cutscenes,
            PSXObjectExporter[] exporters,
            PSXAudioClip[] audioSources,
            PSXSkinnedObjectExporter[] skinnedExporters,
            float gteScaling,
            out long cutsceneTableStart,
            Action<string, LogType> log = null)
        {
            cutsceneTableStart = 0;

            if (cutscenes == null || cutscenes.Length == 0)
                return;

            if (cutscenes.Length > MAX_CUTSCENES)
            {
                log?.Invoke($"Too many cutscenes ({cutscenes.Length} > {MAX_CUTSCENES}). Only the first {MAX_CUTSCENES} will be exported.", LogType.Warning);
                var trimmed = new PSXCutsceneClip[MAX_CUTSCENES];
                Array.Copy(cutscenes, trimmed, MAX_CUTSCENES);
                cutscenes = trimmed;
            }

            // Build audio source name → index lookup
            Dictionary<string, int> audioNameToIndex = new Dictionary<string, int>();
            if (audioSources != null)
            {
                for (int i = 0; i < audioSources.Length; i++)
                {
                    if (!string.IsNullOrEmpty(audioSources[i].ClipName) && !audioNameToIndex.ContainsKey(audioSources[i].ClipName))
                        audioNameToIndex[audioSources[i].ClipName] = i;
                }
            }

            AlignToFourBytes(writer);

            // ── Cutscene Table ──
            cutsceneTableStart = writer.BaseStream.Position;

            // SPLASHPACKCutsceneEntry: 12 bytes each
            // Write placeholders first, then backfill
            long[] entryPositions = new long[cutscenes.Length];
            for (int i = 0; i < cutscenes.Length; i++)
            {
                entryPositions[i] = writer.BaseStream.Position;
                writer.Write((uint)0);  // dataOffset placeholder
                writer.Write((byte)0);  // nameLen placeholder
                writer.Write((byte)0);  // pad
                writer.Write((byte)0);  // pad
                writer.Write((byte)0);  // pad
                writer.Write((uint)0);  // nameOffset placeholder
            }

            // ── Per-cutscene data ──
            for (int ci = 0; ci < cutscenes.Length; ci++)
            {
                PSXCutsceneClip clip = cutscenes[ci];
                AlignToFourBytes(writer);

                // Record data offset
                long dataPos = writer.BaseStream.Position;

                // Validate and clamp
                int trackCount = Mathf.Min(clip.Tracks?.Count ?? 0, MAX_TRACKS);
                int audioEventCount = 0;

                // Count valid audio events
                List<PSXAudioEvent> validEvents = new List<PSXAudioEvent>();
                if (clip.AudioEvents != null)
                {
                    foreach (var evt in clip.AudioEvents)
                    {
                        if (audioNameToIndex.ContainsKey(evt.ClipName))
                        {
                            validEvents.Add(evt);
                            if (validEvents.Count >= MAX_AUDIO_EVENTS) break;
                        }
                        else
                        {
                            log?.Invoke($"Cutscene '{clip.CutsceneName}': audio event clip '{evt.ClipName}' not found in scene audio sources. Skipping.", LogType.Warning);
                        }
                    }
                }
                audioEventCount = validEvents.Count;

                // Sort audio events by frame (required for linear scan on PS1)
                validEvents.Sort((a, b) => a.Frame.CompareTo(b.Frame));

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
                                // Validate clip name exists
                                bool clipFound = false;
                                for (int c = 0; c < skinExp.AnimationClips.Length; c++)
                                {
                                    if (skinExp.AnimationClips[c] != null && skinExp.AnimationClips[c].name == evt.ClipName)
                                    { clipFound = true; break; }
                                }
                                if (clipFound) { found = true; break; }
                                else
                                    log?.Invoke($"Cutscene '{clip.CutsceneName}': skin anim clip '{evt.ClipName}' not found on '{evt.TargetObjectName}'. Skipping.", LogType.Warning);
                            }
                        }
                        if (found) validSkinAnims.Add(evt);
                        else if (!found)
                            log?.Invoke($"Cutscene '{clip.CutsceneName}': skinned object '{evt.TargetObjectName}' not found. Skipping skin anim event.", LogType.Warning);
                        if (validSkinAnims.Count >= MAX_SKIN_ANIM_EVENTS) break;
                    }
                }
                validSkinAnims.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                int skinAnimEventCount = validSkinAnims.Count;

                // SPLASHPACKCutscene: 20 bytes (v19: added skin anim events)
                long tracksOffsetPlaceholder;
                long audioEventsOffsetPlaceholder;
                long skinAnimEventsOffsetPlaceholder;

                writer.Write((ushort)clip.DurationFrames);
                writer.Write((byte)trackCount);
                writer.Write((byte)audioEventCount);
                tracksOffsetPlaceholder = writer.BaseStream.Position;
                writer.Write((uint)0);  // tracksOffset placeholder
                audioEventsOffsetPlaceholder = writer.BaseStream.Position;
                writer.Write((uint)0);  // audioEventsOffset placeholder
                // v19 extension: skin anim events
                writer.Write((byte)skinAnimEventCount);
                writer.Write((byte)0);  // pad
                writer.Write((byte)0);  // pad
                writer.Write((byte)0);  // pad
                skinAnimEventsOffsetPlaceholder = writer.BaseStream.Position;
                writer.Write((uint)0);  // skinAnimEventsOffset placeholder

                // ── Tracks ──
                AlignToFourBytes(writer);
                long tracksStart = writer.BaseStream.Position;

                // SPLASHPACKCutsceneTrack: 12 bytes each
                long[] trackObjectNameOffsets = new long[trackCount];
                long[] trackKeyframesOffsets = new long[trackCount];

                for (int ti = 0; ti < trackCount; ti++)
                {
                    PSXCutsceneTrack track = clip.Tracks[ti];
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

                // ── Keyframe data (per track) ──
                for (int ti = 0; ti < trackCount; ti++)
                {
                    PSXCutsceneTrack track = clip.Tracks[ti];
                    int kfCount = Mathf.Min(track.Keyframes?.Count ?? 0, MAX_KEYFRAMES);

                    AlignToFourBytes(writer);
                    long kfStart = writer.BaseStream.Position;

                    // Sort keyframes by frame
                    var sortedKf = new List<PSXKeyframe>(track.Keyframes ?? new List<PSXKeyframe>());
                    sortedKf.Sort((a, b) => a.Frame.CompareTo(b.Frame));

                    for (int ki = 0; ki < kfCount; ki++)
                    {
                        PSXKeyframe kf = sortedKf[ki];
                        // Pack interp mode into upper 3 bits, frame into lower 13 bits
                        ushort frameAndInterp = (ushort)((((int)kf.Interp & 0x7) << 13) | (kf.Frame & 0x1FFF));
                        writer.Write(frameAndInterp);

                        switch (track.TrackType)
                        {
                            case PSXTrackType.CameraPosition:
                            case PSXTrackType.ObjectPosition:
                            {
                                // Position: convert to fp12, negate Y for PSX coords
                                float gte = gteScaling;
                                short px = PSXTrig.ConvertCoordinateToPSX(kf.Value.x, gte);
                                short py = PSXTrig.ConvertCoordinateToPSX(-kf.Value.y, gte);
                                short pz = PSXTrig.ConvertCoordinateToPSX(kf.Value.z, gte);
                                writer.Write(px);
                                writer.Write(py);
                                writer.Write(pz);
                                break;
                            }
                            case PSXTrackType.CameraRotation:
                            case PSXTrackType.ObjectRotation:
                            {
                                // Rotation: degrees → psyqo::Angle raw (pi-units)
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
                                // Step: values[0] = 0 or 1
                                writer.Write((short)(kf.Value.x > 0.5f ? 1 : 0));
                                writer.Write((short)0);
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.UIProgress:
                            {
                                // values[0] = progress 0-100 as int16
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 0, 100));
                                writer.Write((short)0);
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.UIPosition:
                            {
                                // values[0] = x, values[1] = y (PSX screen coordinates, raw int16)
                                writer.Write((short)Mathf.RoundToInt(kf.Value.x));
                                writer.Write((short)Mathf.RoundToInt(kf.Value.y));
                                writer.Write((short)0);
                                break;
                            }
                            case PSXTrackType.UIColor:
                            {
                                // values[0] = r, values[1] = g, values[2] = b (0-255)
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 0, 255));
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.y), 0, 255));
                                writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.z), 0, 255));
                                break;
                            }
                            case PSXTrackType.CameraH:
                            {
                                // values[0] = H register value (projection distance), 1-1024
                                short h = (short)Mathf.Clamp(Mathf.RoundToInt(kf.Value.x), 1, 1024);
                                writer.Write(h);
                                writer.Write((short)0);
                                writer.Write((short)0);
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

                // ── Object / UI target name strings (per track) ──
                for (int ti = 0; ti < trackCount; ti++)
                {
                    PSXCutsceneTrack track = clip.Tracks[ti];
                    string objName = GetTrackTargetName(track);

                    if (objName.Length > 0)
                    {
                        long namePos = writer.BaseStream.Position;
                        byte[] nameBytes = Encoding.UTF8.GetBytes(objName);
                        writer.Write(nameBytes);
                        writer.Write((byte)0); // null terminator

                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)trackObjectNameOffsets[ti], SeekOrigin.Begin);
                        writer.Write((uint)namePos);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }
                    // else: objectNameOffset stays 0
                }

                // ── Audio events ──
                AlignToFourBytes(writer);
                long audioEventsStart = writer.BaseStream.Position;

                foreach (var evt in validEvents)
                {
                    int clipIdx = audioNameToIndex[evt.ClipName];
                    writer.Write((ushort)evt.Frame);
                    writer.Write((byte)clipIdx);
                    writer.Write((byte)Mathf.Clamp(evt.Volume, 0, 128));
                    writer.Write((byte)Mathf.Clamp(evt.Pan, 0, 127));
                    writer.Write((byte)0); // pad
                    writer.Write((byte)0); // pad
                    writer.Write((byte)0); // pad
                }

                // ── Skin anim events (v19) ──
                AlignToFourBytes(writer);
                long skinAnimEventsStart = writer.BaseStream.Position;

                foreach (var evt in validSkinAnims)
                {
                    // Resolve target object → skinned mesh index
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

                // ── Cutscene name string ──
                string csName = clip.CutsceneName ?? "unnamed";
                if (csName.Length > MAX_NAME_LEN) csName = csName.Substring(0, MAX_NAME_LEN);
                long nameStartPos = writer.BaseStream.Position;
                byte[] csNameBytes = Encoding.UTF8.GetBytes(csName);
                writer.Write(csNameBytes);
                writer.Write((byte)0); // null terminator

                // ── Backfill SPLASHPACKCutscene offsets ──
                {
                    long curPos = writer.BaseStream.Position;

                    // tracksOffset
                    writer.Seek((int)tracksOffsetPlaceholder, SeekOrigin.Begin);
                    writer.Write((uint)tracksStart);
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }
                {
                    long curPos = writer.BaseStream.Position;

                    // audioEventsOffset
                    writer.Seek((int)audioEventsOffsetPlaceholder, SeekOrigin.Begin);
                    writer.Write((uint)(audioEventCount > 0 ? audioEventsStart : 0));
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }
                {
                    long curPos = writer.BaseStream.Position;

                    // skinAnimEventsOffset (v19)
                    writer.Seek((int)skinAnimEventsOffsetPlaceholder, SeekOrigin.Begin);
                    writer.Write((uint)(skinAnimEventCount > 0 ? skinAnimEventsStart : 0));
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }

                // ── Backfill cutscene table entry ──
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)entryPositions[ci], SeekOrigin.Begin);
                    writer.Write((uint)dataPos);                  // dataOffset
                    writer.Write((byte)csNameBytes.Length);        // nameLen
                    writer.Write((byte)0);                        // pad
                    writer.Write((byte)0);                        // pad
                    writer.Write((byte)0);                        // pad
                    writer.Write((uint)nameStartPos);             // nameOffset
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }
            }

            log?.Invoke($"{cutscenes.Length} cutscene(s) exported.", LogType.Log);
        }

        private static void AlignToFourBytes(BinaryWriter writer)
        {
            long pos = writer.BaseStream.Position;
            int padding = (int)(4 - (pos % 4)) % 4;
            if (padding > 0)
                writer.Write(new byte[padding]);
        }

        /// <summary>
        /// Get the target name string for a track.
        /// Camera tracks: empty. Object tracks: ObjectName.
        /// UICanvasVisible: UICanvasName.
        /// UI element tracks: "UICanvasName/UIElementName".
        /// </summary>
        private static string GetTrackTargetName(PSXCutsceneTrack track)
        {
            bool isCameraTrack = track.TrackType == PSXTrackType.CameraPosition ||
                                 track.TrackType == PSXTrackType.CameraRotation ||
                                 track.TrackType == PSXTrackType.CameraH;
            if (isCameraTrack) return "";

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
