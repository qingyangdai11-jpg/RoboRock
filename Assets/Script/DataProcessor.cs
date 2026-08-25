//-----串口通信_Modbus-----//
//-----WEIJI.25.08.12-----//

using System;
using System.Threading;

public class DataProcessor : SerialConnector
{
    public float distance;
    private byte id = 0x01;//通信数据头/从机地址
    private byte[] requestPack;

    #region 线程设置
    protected override void OpenThread()
    {
        dataReceiveThread = new Thread(new ThreadStart(DataReceive));
        dataProcessorThread = new Thread(new ThreadStart(DataProcess));
        requestPackThread = new Thread(new ThreadStart(CallForData));

        requestPackThread.Start();
        dataReceiveThread.Start();
        dataProcessorThread.Start();
    }
    protected override void CloseThread()
    {
        requestPackThread.Abort();
        dataReceiveThread.Abort();
        dataProcessorThread.Abort();
    }
    #endregion

    #region 数据请求实现
    public void RequestPack(byte id, byte fc, byte addrH, byte addrL, byte dinumH, byte dinumL, params byte[] value)
    {
        requestPack = new byte[0];
        uint crc16;
        switch (fc)
        {
            case 0x03:
                requestPack = new byte[8];
                requestPack[0] = id; //从机地址
                requestPack[1] = fc; //功能码
                requestPack[2] = addrH; //起始地址高位
                requestPack[3] = addrL; //起始地址低位
                requestPack[4] = dinumH; //数据长度高位（恒定长度为2）
                requestPack[5] = dinumL;  //数据长度低位

                crc16 = Crc16_Modbus(requestPack, 6);
                requestPack[6] = Convert.ToByte(crc16 & 0xFF);//校验码高位
                requestPack[7] = Convert.ToByte(crc16 / 0x100);//校验码低位
                break;
        }
        this.id = requestPack[0];
        WriteData(requestPack);
        readTextState = false;//允许处理线程开始工作
    }//拼合问询帧包体
    public void CallForData()
    {
        while (true)
        {
            if (sp != null && sp.IsOpen)
            {
                RequestPack(0x01, 0x03, 0x02, 0x01, 0x00, 0x01);
            }
            Thread.Sleep(refreshRate);
        }

    }//按刷新率发送问询帧
    #endregion

    #region 数据处理实现
    
    private uint Crc16_Modbus(byte[] modebusdata, uint length)
    {
        uint i, j;
        uint crc16 = 0xFFFF;

        for (i = 0; i < length; i++)
        {
            crc16 ^= modebusdata[i];  //CRC = BYTE xor CRC（^=取反）
            for (j = 0; j < 8; j++)
            {
                if ((crc16 & 0x01) == 1)  //如果CRC最后一位为1，右移一位后carry=1，则将CRC右移一位后，再与POLY16=0xA001进行xor运算
                {
                    crc16 = (crc16 >> 1) ^ 0xA001;
                }
                else
                {
                    crc16 = crc16 >> 1;   //如果CRC最后一位为0，则只将CRC右移一位
                }
            }
        }

        return crc16;
    }//crc校验算法
    protected override void DataProcessing()
    {
        if (receive[0] == id && !readTextState)
        {
            if (receive.Count >= 3)
            {
                int index = 0;
                message.Add(receive[index++]);//0：地址码
                message.Add(receive[index++]);//1：功能码
                message.Add(receive[index++]);//2：数据长度（恒定为2即读取后两位）

                switch (message[1])
                {

                    case 0x03:
                        int length = message[2];
                        if (receive.Count >= length + 5)
                        {
                            while (index < length + 3)
                            {
                                message.Add(receive[index++]);//3 4：数据包高位，低位 
                            }
                        }
                        else
                        {
                            goto case 0x03;
                        }
                        break;
                }//根据功能码决定行为，当前设备无意义

                message.Add(receive[index++]);//5：校验码高位
                message.Add(receive[index++]);//6：校验码低位
                receive.RemoveRange(0, message.Count);//读完包体，清空对应缓存

                byte[] data = new byte[message.Count - 2];
                message.CopyTo(0, data, 0, data.Length);

                //crc验证
                uint crc16 = Crc16_Modbus(data, (uint)data.Length);
                byte crcH = Convert.ToByte(crc16 & 0xFF);
                byte crcL = Convert.ToByte(crc16 / 0x100);
                
                if ((message[message.Count - 2].Equals(crcH) && message[message.Count - 1].Equals(crcL)) 
                    || (message[message.Count - 1].Equals(crcH) && message[message.Count - 2].Equals(crcL)))
                {
                    byte highByte = message[3];
                    byte lowByte = message[4];
                    ushort result = (ushort)((highByte << 8) | lowByte);//位运算获取数据

                    distance = (float)result;//更新数据

                    readTextState = true;//关闭读取行为                    
                }
                else
                {
                    WriteData(requestPack);//crc验证未通过，重新发送请求包
                }
                message.Clear();
            }
        }
        else
        {
            receive.RemoveAt(0);//移除多余的数据
        }
    }
    #endregion



}
