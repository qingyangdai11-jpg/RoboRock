//-----通用串口脚本基类-----//
//-----WEIJI.25.08.12-----//

//--按需在子类中实现--//
//--线程的开关函数-OpenThread-CloseThread//
//--数据处理函数-DataProcessing--//
//--数据请求函数（一问一答）--//

using System;
using System.IO.Ports;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

public class SerialConnector : MonoBehaviour
{
    #region 定义串口属性
    [Range(1, 20)]
    public int portNumber = 0;
    string portName;//串口名
    public int baudRate = 9600;//波特率
    public int dataBits = 8;//数据位
    public Parity parity = Parity.None;//效验位
    public StopBits stopBits = StopBits.One;//停止位
    [Range(50,1000)]
    public int refreshRate = 50;
    protected SerialPort sp = null;
    #endregion

    #region 定义委托
    public delegate void OnSerialOpen();
    public delegate void OnSerialClose();

    public OnSerialOpen onSerialOpen = null;
    public OnSerialClose onSerialClose = null;
    #endregion

    #region 串口工作相关
    protected Thread dataReceiveThread;
    protected Thread dataProcessorThread;
    protected Thread requestPackThread;

    protected List<byte> receive = new List<byte>();   //接收到的所有消息
    protected List<byte> message = new List<byte>();  //拼合的数据包

    protected bool sendState = false;                 //接收状态
    protected bool readTextState = false;             //读取状态，为false时说明有数据需要处理，允许处理线程工作
    public bool ReadTextState
    {
        get
        {
            return readTextState;
        }
    }
    public bool SendState
    {
        get
        {
            return sendState;
        }
    }

    #endregion

    #region 创建串口，初始化线程
    private void Awake()
    {
        OpenPort();
    }
    protected virtual void OpenThread(){ }
    protected virtual void OpenPort()
    {
        portName = "COM" + portNumber.ToString();
        sp = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
        sp.ReadTimeout = 400;
        try
        {
            sp.Open();
            Debug.Log(portName + "串口开启");
            if(onSerialOpen!=null) onSerialOpen();
            else Debug.LogWarning("当前串口无相关行为");

            OpenThread();
        }
        catch (Exception ex)
        {
            Debug.LogError(portName + "串口开启失败");
            Debug.LogError(ex.Message);
        }
    }
    #endregion

    #region 程序退出时关闭串口
    private void OnApplicationQuit()
    {
        ClosePort();
    }
    protected virtual void CloseThread() { }
    public void ClosePort()
    {
        try
        {
            sp.Close();
            CloseThread();
            if (onSerialClose!=null)onSerialClose();
        }
        catch (Exception ex)
        {
            Debug.Log(ex.Message);
        }
    }
    #endregion

    #region 发送数据
    protected virtual void WriteData(byte[] dataStr)
    {
        if (sp.IsOpen)
        {
            sp.Write(dataStr, 0, dataStr.Length);
        }
    }
    #endregion

    #region 接收数据
    protected void DataReceive()
    {
        byte[] buffer = new byte[1];
        int bytes = 0;
        while (true)
        {
            lock (receive)
            {
                if (sp != null && sp.IsOpen)
                {
                    try
                    {
                        int tmp = sp.ReadBufferSize;
                        for (int i = 0; i < tmp; i++)
                        {
                            bytes = sp.Read(buffer, 0, 1);//接收字节
                            if (bytes == 0)
                            {
                                continue;
                            }
                            else
                            {
                                receive.Add(buffer[0]);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex.GetType() != typeof(ThreadAbortException))
                        {
                        }
                    }
                }
            }
            Thread.Sleep(refreshRate/5);

        }
    }
    #endregion

    #region 处理数据
    protected void DataProcess()
    {
        while (true)
        {
            if (receive.Count > 0)
            {
                DataProcessing();
            }
            Thread.Sleep(refreshRate/5);
        }
    }
    protected virtual void DataProcessing() { }
    #endregion

}
