using System.IO.Ports;


namespace SplashEdit.EditorCode
{
    public class SerialConnection
    {
        private static SerialPort serialPort;

        public SerialConnection(string portName, int baudRate)
        {
            serialPort = new SerialPort(portName, baudRate);
            serialPort.ReadTimeout = 50;
            serialPort.WriteTimeout = 50;
        }

        public void Open()
        { serialPort.Open(); }

        public void Close()
        { serialPort.Close(); }

        public int ReadByte()
        { return serialPort.ReadByte(); }

        public int ReadChar()
        { return serialPort.ReadChar(); }

        public void Write(string text)
        { serialPort.Write(text); }

        public void Write(char[] buffer, int offset, int count)
        { serialPort.Write(buffer, offset, count); }

        public void Write(byte[] buffer, int offset, int count)
        { serialPort.Write(buffer, offset, count); }

    }
}