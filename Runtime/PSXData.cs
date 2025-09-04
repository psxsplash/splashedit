using System.Collections.Generic;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{

    public enum PSXConnectionType
    {
        REAL_HARDWARE, // Unirom
        EMULATOR       // PCSX-Redux
    }

    [CreateAssetMenu(fileName = "PSXData", menuName = "Scriptable Objects/PSXData")]
    public class PSXData : ScriptableObject
    {

        // Texture packing settings
        public Vector2 OutputResolution = new Vector2(320, 240);
        public bool DualBuffering = true;
        public bool VerticalBuffering = true;
        public List<ProhibitedArea> ProhibitedAreas = new List<ProhibitedArea>();


        // Connection settings
        public PSXConnectionType ConnectionType = PSXConnectionType.REAL_HARDWARE;

        // Real hardware settings
        public string PortName = "COM3";
        public int BaudRate = 0;

        // Emulator settings
        public string PCSXReduxPath = "";




    }
}