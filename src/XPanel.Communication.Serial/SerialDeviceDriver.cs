using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using XPanel.Core.Communication;

namespace XPanel.Communication.Serial
{
    /// <summary>
    /// COM 串口通信驱动
    /// </summary>
    public class SerialDeviceDriver : ICommunicationChannel
    {
        private SerialPort _serialPort;
        private CancellationTokenSource _receiveCts;
        private Task _receiveTask;
        private bool _disposed = false;
        private ConnectionState _state = ConnectionState.Disconnected;

        public string ChannelName { get; private set; }

        public ConnectionState State 
        { 
            get => _state;
            private set
            {
                if (_state != value)
                {
                    var oldState = _state;
                    _state = value;
                    OnConnectionStateChanged(oldState, value);
                }
            }
        }

        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<DataReceivedEventArgs> DataReceived;
        public event EventHandler<ErrorEventArgs> ErrorOccurred;

        public SerialDeviceDriver(string portName, int baudRate = 9600, int dataBits = 8, 
            StopBits stopBits = StopBits.One, Parity parity = Parity.None)
        {
            ChannelName = portName;
            _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };
        }

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_serialPort.IsOpen)
                    return true;

                State = ConnectionState.Connecting;
                _serialPort.Open();
                State = ConnectionState.Connected;
                return true;
            }
            catch (Exception ex)
            {
                State = ConnectionState.Failed;
                OnErrorOccurred(ex);
                return false;
            }
        }

        public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                State = ConnectionState.Disconnecting;
                await StopReceivingAsync();

                if (_serialPort.IsOpen)
                    _serialPort.Close();

                State = ConnectionState.Disconnected;
                return true;
            }
            catch (Exception ex)
            {
                State = ConnectionState.Failed;
                OnErrorOccurred(ex);
                return false;
            }
        }

        public async Task<bool> SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (!_serialPort.IsOpen)
                throw new InvalidOperationException("串口未打开");

            try
            {
                _serialPort.Write(data, 0, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                OnErrorOccurred(ex);
                return false;
            }
        }

        public async Task StartReceivingAsync(CancellationToken cancellationToken = default)
        {
            if (_receiveTask != null)
                return;

            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
        }

        public async Task StopReceivingAsync()
        {
            if (_receiveCts != null)
            {
                _receiveCts.Cancel();
                try
                {
                    await _receiveTask;
                }
                catch (OperationCanceledException) { }
                _receiveCts.Dispose();
                _receiveCts = null;
                _receiveTask = null;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024];

            try
            {
                while (!cancellationToken.IsCancellationRequested && _serialPort.IsOpen)
                {
                    if (_serialPort.BytesToRead > 0)
                    {
                        int bytesRead = _serialPort.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            byte[] data = new byte[bytesRead];
                            Array.Copy(buffer, data, bytesRead);
                            OnDataReceived(data);
                        }
                    }
                    else
                    {
                        await Task.Delay(10, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnErrorOccurred(ex);
            }
        }

        protected virtual void OnConnectionStateChanged(ConnectionState oldState, ConnectionState newState)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs 
            { 
                OldState = oldState, 
                NewState = newState,
                Message = $"连接状态从 {oldState} 变为 {newState}"
            });
        }

        protected virtual void OnDataReceived(byte[] data)
        {
            DataReceived?.Invoke(this, new DataReceivedEventArgs 
            { 
                Data = data, 
                ReceiveTime = DateTime.Now 
            });
        }

        protected virtual void OnErrorOccurred(Exception exception)
        {
            ErrorOccurred?.Invoke(this, new ErrorEventArgs 
            { 
                Exception = exception, 
                ErrorMessage = exception.Message 
            });
        }

        public void Dispose()
        {
            if (_disposed) return;

            StopReceivingAsync().Wait();
            _serialPort?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
