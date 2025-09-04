using UnityEngine;
using UnityEditor;
using System.IO.Ports;
using System.Collections;
using SplashEdit.RuntimeCode;
using System.Runtime.InteropServices;

namespace SplashEdit.EditorCode
{
    public class PSXConnectionConfigWindow : EditorWindow
    {

        public PSXConnectionType connectionType = PSXConnectionType.REAL_HARDWARE;

        // REAL HARDWARE (Unirom) SETTINGS
        private string[] portNames;
        private int selectedPortIndex = 1;
        private int[] baudRates = { 9600, 115200 };
        private int selectedBaudIndex = 0;




        private string statusMessage = "";
        private MessageType statusType;
        private Vector2 scrollPosition;

        [MenuItem("PSX/Console or Emulator Connection")]
        public static void ShowWindow()
        {
            GetWindow<PSXConnectionConfigWindow>("Serial Config");
        }

        private void OnEnable()
        {
            RefreshPorts();
            LoadSettings();
        }

        private void RefreshPorts()
        {
            portNames = SerialPort.GetPortNames();
            if (portNames.Length == 0)
            {
                portNames = new[] { "No ports available" };
            }
        }

        private void OnGUI()
        {
            using (var scrollView = new EditorGUILayout.ScrollViewScope(scrollPosition))
            {
                scrollPosition = scrollView.scrollPosition;

                EditorGUILayout.LabelField("Pick connection type", EditorStyles.boldLabel);
                connectionType = (PSXConnectionType)EditorGUILayout.EnumPopup("Connection Type", connectionType);

                if (connectionType == PSXConnectionType.REAL_HARDWARE)
                {
                    // Port selection
                    EditorGUILayout.LabelField("Select COM Port", EditorStyles.boldLabel);
                    selectedPortIndex = EditorGUILayout.Popup("Available Ports", selectedPortIndex, portNames);

                    // Baud rate selection
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Select Baud Rate", EditorStyles.boldLabel);
                    selectedBaudIndex = EditorGUILayout.Popup("Baud Rate", selectedBaudIndex, new[] { "9600", "115200" });

                    // Buttons
                    EditorGUILayout.Space();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Refresh Ports"))
                        {
                            RefreshPorts();
                        }

                        if (GUILayout.Button("Test Connection"))
                        {
                            TestConnection();
                        }
                    }
                }

                if (GUILayout.Button("Save settings"))
                {
                    SaveSettings();
                    
                }

                // Status message
                EditorGUILayout.Space();
                if (!string.IsNullOrEmpty(statusMessage))
                {
                    EditorGUILayout.HelpBox(statusMessage, statusType);
                }

            }
        }

        private void LoadSettings()
        {
            PSXData _psxData = DataStorage.LoadData();
            if (_psxData != null)
            {
                connectionType = _psxData.ConnectionType;
                selectedBaudIndex = System.Array.IndexOf(baudRates, _psxData.BaudRate);
                if (selectedBaudIndex == -1) selectedBaudIndex = 0;

                RefreshPorts();
                selectedPortIndex = System.Array.IndexOf(portNames, _psxData.PortName);
                if (selectedPortIndex == -1) selectedPortIndex = 0;
            }
        }

        private void TestConnection()
        {
            if (portNames.Length == 0 || portNames[0] == "No ports available")
            {
                statusMessage = "No serial ports available";
                statusType = MessageType.Error;
                return;
            }

            UniromConnection connection = new UniromConnection(baudRates[selectedBaudIndex], portNames[selectedPortIndex]);
            connection.Reset();

            statusMessage = "Connection tested. If your PlayStation reset, it worked!";
            statusType = MessageType.Info;
            Repaint();
        }

        private void SaveSettings()
        {
            PSXData _psxData = DataStorage.LoadData();
            _psxData.ConnectionType = connectionType;
            _psxData.BaudRate = baudRates[selectedBaudIndex];
            _psxData.PortName = portNames[selectedPortIndex];
            DataStorage.StoreData(_psxData);
            statusMessage = "Settings saved";
            statusType = MessageType.Info;
            Repaint();
        }
    }
}