﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WatsonTcp.Message
{
    public class WatsonMessage
    {
        #region Public-Members

        /// <summary>
        /// Length of all header fields and payload data.
        /// </summary>
        public long Length { get; set; }

        public BitArray HeaderFields { get; set; }   // 8 bytes
        public byte[] PresharedKey { get; set; }     // HeaderFields[0], 16 bytes
        public MessageStatus Status { get; set; }    // HeaderFields[1], 4 bytes

        public byte[] Data { get; set; }

        /// <summary>
        /// Size of buffer to use while reading message payload.  Default is 64KB.
        /// </summary>
        public int ReadBuffer
        {
            get
            {
                return _ReadBuffer;
            }
            set
            {
                _ReadBuffer = value;
            }
        }

        #endregion

        #region Private-Members

        private bool _Debug = false;

        //                                123456789012345678901234567890
        private string _DateTimeFormat = "MMddyyyyTHHmmssffffffz"; // 22 bytes

        private NetworkStream _NetworkStream;
        private SslStream _SslStream;
        private int _ReadBuffer = 65536;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Do not use.
        /// </summary>
        public WatsonMessage()
        {
            HeaderFields = new BitArray(64);
            InitBitArray(HeaderFields);
            Status = MessageStatus.Normal;
        }

        /// <summary>
        /// Construct a new message to send.
        /// </summary>
        /// <param name="data"></param>
        public WatsonMessage(byte[] data, bool debug)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));

            HeaderFields = new BitArray(64);
            InitBitArray(HeaderFields);

            Status = MessageStatus.Normal;

            Data = new byte[data.Length];
            Buffer.BlockCopy(data, 0, Data, 0, data.Length);

            _Debug = debug;
        }

        /// <summary>
        /// Instantiate the object using a TCP-based stream.  Call Build() to populate.
        /// </summary>
        /// <param name="stream">NetworkStream.</param>
        /// <param name="debug">Enable or disable console debugging.</param>
        public WatsonMessage(NetworkStream stream, bool debug)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("Cannot read from stream.");

            HeaderFields = new BitArray(64);
            InitBitArray(HeaderFields);
            Status = MessageStatus.Normal;

            _NetworkStream = stream;
            _Debug = debug;
        }

        /// <summary>
        /// Instantiate the object using an SSL-based stream.  Call Build() to populate.
        /// </summary>
        /// <param name="stream">SslStream.</param>
        /// <param name="debug">Enable or disable console debugging.</param>
        public WatsonMessage(SslStream stream, bool debug)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("Cannot read from stream.");

            HeaderFields = new BitArray(64);
            InitBitArray(HeaderFields);
            Status = MessageStatus.Normal;

            _SslStream = stream;
            _Debug = debug;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Awaitable async method to build the Message object from data that awaits in a NetworkStream or SslStream.  
        /// </summary>
        /// <returns>Always returns true (void cannot be a return parameter).</returns>
        public async Task<bool> Build()
        { 
            try
            {
                int read = 0;
                int totalBytesRead = 0;

                #region Read-Message-Length

                using (MemoryStream msgLengthMs = new MemoryStream())
                { 
                    byte[] msgLengthBuffer = new byte[1];  

                    if (_NetworkStream != null)
                    {
                        while ((read = await _NetworkStream.ReadAsync(msgLengthBuffer, 0, msgLengthBuffer.Length)) > 0)
                        {
                            await msgLengthMs.WriteAsync(msgLengthBuffer, 0, read); 
                             
                            // check if end of headers reached
                            if (msgLengthBuffer[0] == 58)
                            {
                                break;
                            } 
                        }
                    }
                    else if (_SslStream != null)
                    {
                        while ((read = await _SslStream.ReadAsync(msgLengthBuffer, 0, msgLengthBuffer.Length)) > 0)
                        {
                            await msgLengthMs.WriteAsync(msgLengthBuffer, 0, read);
                            totalBytesRead += read;

                            // check if end of headers reached
                            if (msgLengthBuffer[0] == 58)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        throw new ArgumentException("Unknown stream type.");
                    }

                    byte[] msgLengthBytes = msgLengthMs.ToArray();
                    if (msgLengthBytes == null || msgLengthBytes.Length < 1) return false;
                    string msgLengthString = Encoding.UTF8.GetString(msgLengthBytes).Replace(":", "");

                    long length;
                    Int64.TryParse(msgLengthString, out length);
                    Length = length;

                    if (_Debug) Console.WriteLine("Message payload length: " + Length + " bytes");
                }

                #endregion
                 
                #region Process-Header-Fields

                byte[] headerFields = await ReadFromNetwork(8, "HeaderFields");
                headerFields = ReverseByteArray(headerFields);
                HeaderFields = new BitArray(headerFields);

                long payloadBytes = Length - 8;

                for (int i = 0; i < HeaderFields.Length; i++)
                {
                    if (HeaderFields[i])
                    {
                        MessageField field = GetMessageField(i);
                        if (_Debug) Console.WriteLine("Reading header field " + i + " " + field.Name + " " + field.Type.ToString() + " " + field.Length + " bytes");
                        object val = await ReadField(field.Type, field.Length, field.Name);
                        SetMessageValue(field, val);
                        payloadBytes -= field.Length;
                    }
                }
                 
                Data = await ReadFromNetwork(payloadBytes, "Payload");

                #endregion
                 
                return true;
            }
            catch (Exception e)
            {
                if (_Debug) Console.WriteLine("Message build exception: " + e.Message); 
                throw;
            }
            finally
            {
                if (_Debug) Console.WriteLine("Message build completed");
            }
        }

        /// <summary>
        /// Creates a byte array useful for transmission from the object.
        /// </summary>
        /// <returns>Byte array.</returns>
        public byte[] ToBytes()
        {
            SetHeaderFieldBitmap();

            byte[] headerFieldsBytes = new byte[8];
            headerFieldsBytes = BitArrayToBytes(HeaderFields);
            headerFieldsBytes = ReverseByteArray(headerFieldsBytes);

            byte[] ret = new byte[headerFieldsBytes.Length];
            Buffer.BlockCopy(headerFieldsBytes, 0, ret, 0, headerFieldsBytes.Length);
             
            #region Header-Fields
             
            for (int i = 0; i < HeaderFields.Length; i++)
            {
                if (HeaderFields[i])
                {
                    MessageField field = GetMessageField(i);
                    switch (i)
                    {
                        case 0:
                            ret = AppendBytes(ret, PresharedKey);
                            break;
                        case 1:
                            if (_Debug)
                            {
                                Console.WriteLine("Status: " + Status.ToString() + " " + (int)Status);
                            }
                            ret = AppendBytes(ret, IntegerToBytes((int)Status));
                            break;
                        default:
                            throw new ArgumentException("Unknown bit number.");
                    }
                }
            }

            #endregion

            #region Payload-Data-and-Length

            if (Data != null && Data.Length > 0)
            {
                ret = AppendBytes(ret, Data);
            }

            long finalLen = ret.Length;

            byte[] lengthHeader = Encoding.UTF8.GetBytes(finalLen.ToString() + ":");
            byte[] final = new byte[(lengthHeader.Length + ret.Length)];
            Buffer.BlockCopy(lengthHeader, 0, final, 0, lengthHeader.Length);
            Buffer.BlockCopy(ret, 0, final, lengthHeader.Length, ret.Length);

            #endregion
             
            return final;
        }

        public override string ToString()
        {
            string ret = "---" + Environment.NewLine;
            ret += "  Header fields : " + FieldToString(FieldType.Bits, HeaderFields) + Environment.NewLine;
            ret += "  Preshared key : " + FieldToString(FieldType.ByteArray, PresharedKey) + Environment.NewLine;
            ret += "  Status        : " + FieldToString(FieldType.Int32, (int)Status) + Environment.NewLine;
            ret += "  Data          : " + Data.Length + " bytes" + Environment.NewLine;
            return ret;
        }

        #endregion

        #region Private-Methods

        private void SetHeaderFieldBitmap()
        {
            HeaderFields = new BitArray(64);
            InitBitArray(HeaderFields);

            if (PresharedKey != null && PresharedKey.Length > 0) HeaderFields[0] = true;
            HeaderFields[1] = true;  // messages will always have a status
        }

        private async Task<object> ReadField(FieldType fieldType, int maxLength, string name)
        {
            string logMessage = "ReadField " + fieldType.ToString() + " " + maxLength + " " + name;

            try
            { 
                byte[] data = null;
                int headerLength = 0;

                object ret = null;

                if (fieldType == FieldType.Int32)
                {
                    data = await ReadFromNetwork(maxLength, name + " Int32 (" + maxLength + ")");
                    logMessage += " " + ByteArrayToHex(data);
                    ret = Convert.ToInt32(Encoding.UTF8.GetString(data));
                    logMessage += ": " + ret;
                }
                else if (fieldType == FieldType.Int64)
                {
                    data = await ReadFromNetwork(maxLength, name + " Int64 (" + maxLength + ")");
                    logMessage += " " + ByteArrayToHex(data);
                    ret = Convert.ToInt64(Encoding.UTF8.GetString(data));
                    logMessage += ": " + ret;
                }
                else if (fieldType == FieldType.String)
                {
                    data = await ReadFromNetwork(maxLength, name + " String (" + maxLength + ")");
                    logMessage += " " + ByteArrayToHex(data);
                    ret = Encoding.UTF8.GetString(data); 
                    logMessage += ": " + headerLength + " " + ret;
                } 
                else if (fieldType == FieldType.DateTime)
                {
                    data = await ReadFromNetwork(22, name + " DateTime");
                    logMessage += " " + ByteArrayToHex(data);
                    ret = DateTime.ParseExact(Encoding.UTF8.GetString(data), _DateTimeFormat, CultureInfo.InvariantCulture);
                    logMessage += ": " + headerLength + " " + ret.ToString();
                } 
                else if (fieldType == FieldType.ByteArray)
                {
                    ret = await ReadFromNetwork(maxLength, name + " ByteArray (" + maxLength + ")");
                    logMessage += " " + ByteArrayToHex((byte[])ret);
                    logMessage += ": " + headerLength + " " + ByteArrayToHex((byte[])ret);
                }
                else
                {
                    throw new ArgumentException("Unknown field type: " + fieldType.ToString());
                }

                return ret;
            }
            finally
            {
                Debug.WriteLine(logMessage);
            }
        }

        private byte[] FieldToBytes(FieldType fieldType, object data, int maxLength)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (fieldType == FieldType.Int32)
            {
                int intVar = Convert.ToInt32(data);
                string lengthVar = "";
                for (int i = 0; i < maxLength; i++) lengthVar += "0";
                return Encoding.UTF8.GetBytes(intVar.ToString(lengthVar));
            }
            else if (fieldType == FieldType.Int64)
            {
                long longVar = Convert.ToInt64(data);
                string lengthVar = "";
                for (int i = 0; i < maxLength; i++) lengthVar += "0";
                return Encoding.UTF8.GetBytes(longVar.ToString(lengthVar));
            } 
            else if (fieldType == FieldType.String)
            {
                string dataStr = data.ToString().ToUpper();
                if (dataStr.Length < maxLength)
                {
                    string ret = dataStr.PadRight(maxLength);
                    return Encoding.UTF8.GetBytes(ret);
                }
                else if (dataStr.Length > maxLength)
                {
                    string ret = dataStr.Substring(maxLength);
                    return Encoding.UTF8.GetBytes(ret);
                }
                else
                {
                    return Encoding.UTF8.GetBytes(dataStr);
                }
            }
            else if (fieldType == FieldType.DateTime)
            {
                string dateTime = Convert.ToDateTime(data).ToString(_DateTimeFormat);
                return Encoding.UTF8.GetBytes(dateTime);
            }
            else if (fieldType == FieldType.ByteArray)
            {
                if (((byte[])data).Length != maxLength) throw new ArgumentException("Data length does not match length supplied.");

                byte[] ret = new byte[maxLength];
                InitByteArray(ret);
                Buffer.BlockCopy((byte[])data, 0, ret, 0, maxLength);
                return ret;
            } 
            else
            {
                throw new ArgumentException("Unknown field type: " + fieldType.ToString());
            }
        }

        private string FieldToString(FieldType fieldType, object data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (fieldType == FieldType.Int32)
            {
                return "[i]" + data.ToString();
            }
            else if (fieldType == FieldType.Int64)
            {
                return "[l]" + data.ToString();
            }
            else if (fieldType == FieldType.String)
            {
                return "[s]" + data.ToString();
            } 
            else if (fieldType == FieldType.DateTime)
            {
                return "[d]" + Convert.ToDateTime(data).ToString(_DateTimeFormat);
            } 
            else if (fieldType == FieldType.ByteArray)
            {
                return "[b]" + ByteArrayToHex((byte[])data);
            }
            else if (fieldType == FieldType.Bits)
            {
                if (data is BitArray)
                {
                    data = BitArrayToBytes((BitArray)data);
                }

                string[] s = ((byte[])data).Select(x => Convert.ToString(x, 2).PadLeft(8, '0')).ToArray();
                string ret = "[b]" + ByteArrayToHex((byte[])data) + ": ";
                foreach (string curr in s)
                {
                    char[] ca = curr.ToCharArray();
                    Array.Reverse(ca);
                    ret += new string(ca) + " ";
                }
                return ret;
            }
            else
            {
                throw new ArgumentException("Unknown field type: " + fieldType.ToString());
            }
        }

        private async Task<byte[]> ReadFromNetwork(long count, string field)
        {
            string logMessage = "ReadFromNetwork " + count + " " + field;
            if (_Debug) Console.WriteLine(logMessage);

            try
            {
                if (count <= 0) return null;
                int read = 0;
                byte[] buffer = new byte[count];
                byte[] ret = null;

                InitByteArray(buffer);

                if (_NetworkStream != null)
                { 
                    while (true)
                    {
                        read = await _NetworkStream.ReadAsync(buffer, 0, buffer.Length);
                        if (read == count)
                        {
                            ret = new byte[read];
                            Buffer.BlockCopy(buffer, 0, ret, 0, read);
                            break;
                        }
                    } 
                }
                else if (_SslStream != null)
                { 
                    while (true)
                    {
                        read = await _SslStream.ReadAsync(buffer, 0, buffer.Length);
                        if (read == count)
                        {
                            ret = new byte[read];
                            Buffer.BlockCopy(buffer, 0, ret, 0, read);
                            break;
                        }
                    } 
                } 
                else
                {
                    throw new IOException("No suitable input stream found.");
                }

                if (ret != null && ret.Length > 0) logMessage += ": " + ByteArrayToHex(ret);
                else logMessage += ": (null)";

                return ret;
            }
            finally
            {
                if (_Debug) Console.WriteLine(logMessage);
            }
        }

        private byte[] IntegerToBytes(int i)
        {
            if (i < 0 || i > 9999) throw new ArgumentException("Integer must be between 0 and 9999.");

            byte[] ret = new byte[4];
            InitByteArray(ret);

            string stringVal = i.ToString("0000");

            ret[3] = (byte)(Convert.ToInt32(stringVal[3]));
            ret[2] = (byte)(Convert.ToInt32(stringVal[2]));
            ret[1] = (byte)(Convert.ToInt32(stringVal[1]));
            ret[0] = (byte)(Convert.ToInt32(stringVal[0]));

            return ret;
        }

        private int BytesToInteger(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1) throw new ArgumentNullException(nameof(bytes));

            // see https://stackoverflow.com/questions/36295952/direct-convertation-between-ascii-byte-and-int?rq=1

            int result = 0;

            for (int i = 0; i < bytes.Length; ++i)
            {
                // ASCII digits are in the range 48 <= n <= 57. This code only
                // makes sense if we are dealing exclusively with digits, so
                // throw if we encounter a non-digit character
                if (bytes[i] < 48 || bytes[i] > 57)
                {
                    throw new ArgumentException("Non-digit character present.");
                }

                // The bytes are in order from most to least significant, so
                // we need to reverse the index to get the right column number
                int exp = bytes.Length - i - 1;

                // Digits in ASCII start with 0 at 48, and move sequentially
                // to 9 at 57, so we can simply subtract 48 from a valid digit
                // to get its numeric value
                int digitValue = bytes[i] - 48;

                // Finally, add the digit value times the column value to the
                // result accumulator
                result += digitValue * (int)Math.Pow(10, exp);
            }

            return result;
        }

        private void InitByteArray(byte[] data)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = 0x00;
            }
        }

        private void InitBitArray(BitArray data)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = false;
            }
        }

        private byte[] AppendBytes(byte[] head, byte[] tail)
        {
            byte[] arrayCombined = new byte[head.Length + tail.Length];
            Array.Copy(head, 0, arrayCombined, 0, head.Length);
            Array.Copy(tail, 0, arrayCombined, head.Length, tail.Length);
            return arrayCombined;
        }

        private string ByteArrayToHex(byte[] data)
        {
            StringBuilder hex = new StringBuilder(data.Length * 2);
            foreach (byte b in data) hex.AppendFormat("{0:x2}", b);
            return hex.ToString();
        }

        private void ReverseBitArray(BitArray array)
        {
            int length = array.Length;
            int mid = (length / 2);

            for (int i = 0; i < mid; i++)
            {
                bool bit = array[i];
                array[i] = array[length - i - 1];
                array[length - i - 1] = bit;
            }
        }

        private byte[] ReverseByteArray(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1) throw new ArgumentNullException(nameof(bytes));

            byte[] ret = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                ret[i] = ReverseByte(bytes[i]);
            }

            return ret;
        }

        private byte ReverseByte(byte b)
        {
            return (byte)(((b * 0x0802u & 0x22110u) | (b * 0x8020u & 0x88440u)) * 0x10101u >> 16);
        }

        private byte[] BitArrayToBytes(BitArray bits)
        {
            if (bits == null || bits.Length < 1) throw new ArgumentNullException(nameof(bits));
            if (bits.Length % 8 != 0) throw new ArgumentException("BitArray length must be divisible by 8.");

            byte[] ret = new byte[(bits.Length - 1) / 8 + 1];
            bits.CopyTo(ret, 0);
            return ret;
        }

        private MessageField GetMessageField(int bitNumber)
        {
            switch (bitNumber)
            {
                case 0:
                    return new MessageField(0, "PresharedKey", FieldType.ByteArray, 16);
                case 1:
                    return new MessageField(1, "Status", FieldType.Int32, 4);
                default:
                    throw new KeyNotFoundException();
            }
        }

        private void SetMessageValue(MessageField field, object val)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (val == null) throw new ArgumentNullException(nameof(val));

            switch (field.BitNumber)
            {
                case 0:
                    PresharedKey = (byte[])val;
                    if (_Debug) Console.WriteLine("PresharedKey: " + PresharedKey);
                    return;
                case 1:
                    Status = (MessageStatus)((int)val);
                    if (_Debug) Console.WriteLine("Status: " + Status.ToString());
                    return;
                default:
                    throw new ArgumentException("Unknown bit number.");
            }  
        }
         
        #endregion
    }
}
