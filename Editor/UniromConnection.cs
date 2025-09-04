namespace SplashEdit.EditorCode
{
    public class UniromConnection
    {

        private SerialConnection serialConnection;

        public UniromConnection(int baudRate, string portName)
        {
            serialConnection = new SerialConnection(portName, baudRate);
        }

        public void Reset()
        {
            serialConnection.Open();
            serialConnection.Write("REST");
            serialConnection.Close();
        }


    }
}