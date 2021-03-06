﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Timers;
using bitkyFlashresUniversal.connClient.model.bean;
using bitkyFlashresUniversal.connClient.model.commtUtil;
using bitkyFlashresUniversal.connClient.model.commtUtil.ConnClient;
using bitkyFlashresUniversal.connClient.model.tiaozhanbei;
using bitkyFlashresUniversal.connClient.presenter;
using Timer = System.Timers.Timer;

namespace bitkyFlashresUniversal.connClient.model
{
    /// <summary>
    ///     通信的外观模式实现类
    /// </summary>
    public class CommucationFacade : ICommucationFacade
    {
        private readonly ControlFrameBuilder _controlFrameBuilder; //控制帧构建器
        private readonly List<Electrode> _electrodes = new List<Electrode>();

        private readonly FrameProvider _frameProvider; //远程数据初步解析器，解析为数据帧
        private readonly BitkyTcpClient _myTcpClient; //TCP客户端
        private readonly SerialPortClient _myPortClient; //串口客户端
        private readonly ICommPresenter _presenter; //通信接口表现层

        private readonly Timer _timerFrameCollect;
        private int _sendDeviceResetTimeout = 0;
        private bool _connIsOpen = false;

        private FrameData _currentframeData;

        Random _random = new Random();

        public CommucationFacade(ICommPresenter presenter)
        {
            _presenter = presenter;
            _myTcpClient = new BitkyTcpClient(this);
            _myPortClient = new SerialPortClient(this);
            _controlFrameBuilder = new ControlFrameBuilder();
            _frameProvider = new FrameProvider();

            //子帧收集计时器
            _timerFrameCollect = new Timer(PresetInfo.FrameReceiveTimeout) {AutoReset = false};
            _timerFrameCollect.Elapsed += FrameCollect;
        }

        /// <summary>
        ///     使用指定的IP地址和端口号构建TCP客户端
        /// 或  使用指定的串口名和波特率构建串口客户端
        /// </summary>
        /// <param name="str"></param>
        /// <param name="num"></param>
        public void InitCommClient(string str, int num)
        {
            CommMsg.SwitchReveiveFrame();
            switch (PresetInfo.CurrentCommType)
            {
                case CommType.Wifi:
                    _myTcpClient.Build(str, num);

                    break;
                case CommType.SerialPort:
                    _myPortClient.Open(str, num);

                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }


        /// <summary>
        ///     通信连接失效
        /// </summary>
        public void CommClientFailed(string str)
        {
            _connIsOpen = false;
            MessageBeep(0x00000030);
            _timerFrameCollect.Stop();
            _sendDeviceResetTimeout = 0;
            switch (PresetInfo.CurrentCommType)
            {
                case CommType.Wifi:
                    _myTcpClient.Close();
                    break;
                case CommType.SerialPort:
                    _myPortClient.Close();
                    break;

//                default:
//                    throw new ArgumentOutOfRangeException();
            }

            _presenter.ConnFailed(str);


            PresetInfo.CurrentCommType = CommType.Null;
        }

        /// <summary>
        ///     建立连接成功，获取Socket成功的消息
        /// </summary>
        public void GetSocketSuccess()
        {
            _connIsOpen = true;
            _presenter.GetSocketSuccess();
        }

        /// <summary>
        ///     从TCP客户端获取已接收到的数据
        /// </summary>
        /// <param name="data">获取的远程数据</param>
        public void GetReceivedData(byte[] data)
        {
            //--------挑战杯用，删除即可恢复-------------
            List<byte> bytes = SendDataTZB.process(data);
            if (bytes == null)
            {
                return;
            }
            data = bytes.ToArray();
            //--------挑战杯用，删除即可恢复-------------

            //接收到的数据显示
            var stringbuilder = new StringBuilder();
            foreach (var t in data)
                stringbuilder.Append($"{t:X2}" + " ");
            _presenter.ReceiveDataShow("已接收:" + stringbuilder);
            Debug.WriteLine("已接收:" + stringbuilder);

            var frameData = _frameProvider.ObtainData(data);
            SetFrameData(frameData);
        }

        /// <summary>
        ///     返回程序处理后的数据帧
        /// </summary>
        /// <param name="frameData">处理后的数据帧</param>
        private void SetFrameData(FrameData frameData)
        {
            //维护帧类型：握手帧、数据帧头、返回数据帧头
            _presenter.SetFrameData(frameData);
            switch (frameData.Type)
            {
                case FrameType.HandshakeSwitchDevice:
                    _presenter.CommunicateMessageShow("收到握手帧的回复");
                    break;

                case FrameType.ReturnDataGather:
                    _timerFrameCollect.Stop();
                    _sendDeviceResetTimeout = 0;
                    _electrodes.Clear();

                    frameData.PoleList.ForEach(pole =>
                    {
                        if (pole.IdOrigin > 79 || pole.IdOrigin < 0)
                        {
                            _presenter.CommunicateMessageShow("收到的帧通道号错误");
                            return;
                        }
                        if (pole.IdOrigin <= 63)
                        {
                            //        pole.Value = (pole.Value * 5 / 16777216 - 2.5) * 2000;
                            pole.Value = pole.Value + _random.NextDouble() * 0.1;
                            _electrodes.Add(pole);
                        }

                        if (pole.IdOrigin == 64)
                        {
                            //            pole.Value = Math.Abs((pole.Value * 5 / 16777216 - 2.5) * 100 / 1.25);
                            pole.Value = pole.Value + _random.NextDouble() * 0.1;
                            _electrodes.Add(pole);
                        }
                    });
                    _presenter.InsertDataIntoDb(_electrodes);
                    break;

                case FrameType.HvRelayOpen:
                    _presenter.CommunicateMessageShow("高压继电器控制成功");
                    break;
                case FrameType.None:
                    _presenter.CommunicateMessageShow("收到的帧错误");
                    break;
            }
        }

        /// <summary>
        ///     发送指定的帧
        /// </summary>
        /// <param name="frameData">指定的帧格式</param>
        public void SendDataFrame(FrameData frameData)
        {
            if (!_connIsOpen)
            {
                _presenter.CommunicateMessageShow("连接已断开");
                return;
            }
            _currentframeData = frameData;
            if (frameData.Type != FrameType.ControlGather)
            {
                _sendDeviceResetTimeout = 0;
            }
            switch (frameData.Type)
            {
                case FrameType.HvRelayOpen:
                    Send(CommMsg.HvRelayOpen);
                    break;
                case FrameType.HvRelayClose:
                    Send(CommMsg.HvRelayClose);
                    break;
                case FrameType.HandshakeSwitchDevice:
                    Send(CommMsg.CurrentReceiveSwitchFrame);
                    break;
                case FrameType.DeviceReset:
                    Send(CommMsg.DeviceResetFrame);
                    break;
                case FrameType.ControlGather:
                    Send(_controlFrameBuilder.DataFrameBuild(_currentframeData));
                    _timerFrameCollect.Interval = PresetInfo.FrameReceiveTimeout;
                    _timerFrameCollect.Start();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void Send(byte[] data)
        {
            switch (PresetInfo.CurrentCommType)
            {
                case CommType.Wifi:
                    _myTcpClient.Send(data);
                    break;
                case CommType.SerialPort:
                    _myPortClient.Send(data);
                    break;
//                default:
//                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        ///     发送帧信息的显示
        /// </summary>
        /// <param name="message">输入所需显示的信息</param>
        public void SendDataShow(string message)
        {
            _presenter.SendDataShow(message);
        }

        /// <summary>
        ///     子帧采集没有完成事件的回调
        /// </summary>
        private void FrameCollect(object source, ElapsedEventArgs e)
        {
            MessageBeep(0x00000030);
            //  MessageBox.Show("确认后继续");
            if (!_connIsOpen)
            {
                _presenter.CommunicateMessageShow("连接已断开，程序停止");
                return;
            }
            if (!PresetInfo.StartAutoCollect)
            {
                return;
            }
            _presenter.CommunicateMessageShow("未接收到正确的子帧数据,程序继续");
            _sendDeviceResetTimeout++;
            if (_sendDeviceResetTimeout >= 3)
            {
                Send(CommMsg.DeviceResetFrame);
                Debug.WriteLine("已发送重置帧");
                _sendDeviceResetTimeout = 0;
                Thread.Sleep(1000);
            }
            SendDataFrame(_currentframeData);
        }

        [DllImport("user32.dll ")]
        private static extern int MessageBeep(uint n);
    }
}