// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

#if !NETSTANDARD1_6
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using Pomelo.Data.MySql;
using System.Diagnostics;


namespace Pomelo.Data.Common
{
#if !PocketPC

  /// <summary>
  /// Helper class to encapsulate shared memory functionality
  /// Also cares of proper cleanup of file mapping object and cew
  /// </summary>
  internal class SharedMemory : IDisposable
  {
    private const uint FILE_MAP_WRITE = 0x0002;

    IntPtr fileMapping;
    IntPtr view;

    public SharedMemory(string name, IntPtr size)
    {
      fileMapping = NativeMethods.OpenFileMapping(FILE_MAP_WRITE, false,
          name);
      if (fileMapping == IntPtr.Zero)
      {
        throw new MySqlException("Cannot open file mapping " + name);
      }
      view = NativeMethods.MapViewOfFile(fileMapping, FILE_MAP_WRITE, 0, 0, size);
    }

    #region Destructor
    ~SharedMemory()
    {
      Dispose(false);
    } 
    #endregion

    public IntPtr View
    {
      get { return view; }
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }


    protected virtual void Dispose(bool disposing)
    {
      if (disposing)
      {
        if (view != IntPtr.Zero)
        {
          NativeMethods.UnmapViewOfFile(view);
          view = IntPtr.Zero;
        }
        if (fileMapping != IntPtr.Zero)
        {
          // Free the handle
          NativeMethods.CloseHandle(fileMapping);
          fileMapping = IntPtr.Zero;
        }
      }
    }

  }
  /// <summary>
  /// Summary description for SharedMemoryStream.
  /// </summary>
  internal class SharedMemoryStream : Stream
  {
    private string memoryName;
    private EventWaitHandle serverRead;
    private EventWaitHandle serverWrote;
    private EventWaitHandle clientRead;
    private EventWaitHandle clientWrote;
    private EventWaitHandle connectionClosed;
    private SharedMemory data;
    private int bytesLeft;
    private int position;
    private int connectNumber;

    private const int BUFFERLENGTH = 16004;

    private int readTimeout = System.Threading.Timeout.Infinite;
    private int writeTimeout = System.Threading.Timeout.Infinite;

    public SharedMemoryStream(string memName)
    {      
      memoryName = memName;
    }

    public void Open(uint timeOut)
    {
      if (connectionClosed != null)
      {
        Debug.Assert(false, "Connection is already open");
      }
      
      GetConnectNumber(timeOut);
      SetupEvents();
    }

    public override void Close()
    {
      if (connectionClosed != null)
      {
        bool isClosed = connectionClosed.WaitOne(0);
        if (!isClosed)
        {
          connectionClosed.Set();
          connectionClosed.Close();
        }
        connectionClosed = null;
        EventWaitHandle[] handles = { serverRead, serverWrote, clientRead, clientWrote };

        for (int i = 0; i < handles.Length; i++)
        {
          if (handles[i] != null)
            handles[i].Close();
        }
        if (data != null)
        {
          data.Dispose();
          data = null;
        }
      }
    }

    private void GetConnectNumber(uint timeOut)
    {
      EventWaitHandle connectRequest;
      try
      {
        connectRequest =
            EventWaitHandle.OpenExisting(memoryName + "_CONNECT_REQUEST");

      }
      catch (Exception)
      {
        // If server runs as service, its shared memory is global 
        // And if connector runs in user session, it needs to prefix
        // shared memory name with "Global\"
        string prefixedMemoryName = @"Global\" + memoryName;
        connectRequest =
            EventWaitHandle.OpenExisting(prefixedMemoryName + "_CONNECT_REQUEST");
        memoryName = prefixedMemoryName;
      }
      EventWaitHandle connectAnswer =
         EventWaitHandle.OpenExisting(memoryName + "_CONNECT_ANSWER");
      using (SharedMemory connectData =
          new SharedMemory(memoryName + "_CONNECT_DATA", (IntPtr)4))
      {
        // now start the connection
        if (!connectRequest.Set())
          throw new MySqlException("Failed to open shared memory connection");
        if (!connectAnswer.WaitOne((int)(timeOut * 1000), false))
          throw new MySqlException("Timeout during connection");
        connectNumber = Marshal.ReadInt32(connectData.View);
      }
    }


    private void SetupEvents()
    {
      string prefix = memoryName + "_" + connectNumber;
      data = new SharedMemory(prefix + "_DATA", (IntPtr)BUFFERLENGTH);
      serverWrote = EventWaitHandle.OpenExisting(prefix + "_SERVER_WROTE");
      serverRead = EventWaitHandle.OpenExisting(prefix + "_SERVER_READ");
      clientWrote = EventWaitHandle.OpenExisting(prefix + "_CLIENT_WROTE");
      clientRead = EventWaitHandle.OpenExisting(prefix + "_CLIENT_READ");
      connectionClosed = EventWaitHandle.OpenExisting(prefix + "_CONNECTION_CLOSED");

      // tell the server we are ready
      serverRead.Set();
    }

    #region Properties
    public override bool CanRead
    {
      get { return true; }
    }

    public override bool CanSeek
    {
      get { return false; }
    }

    public override bool CanWrite
    {
      get { return true; }
    }

    public override long Length
    {
      get { throw new NotSupportedException("SharedMemoryStream does not support seeking - length"); }
    }

    public override long Position
    {
      get { throw new NotSupportedException("SharedMemoryStream does not support seeking - position"); }
      set { }
    }

    #endregion

    public override void Flush()
    {
      // No need to flush anything to disk ,as our shared memory is backed 
      // by the page file
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
      int timeLeft = readTimeout;
      WaitHandle[] waitHandles = { serverWrote, connectionClosed };
      LowResolutionStopwatch stopwatch = new LowResolutionStopwatch();
      while (bytesLeft == 0)
      {
        stopwatch.Start();
        int index = WaitHandle.WaitAny(waitHandles, timeLeft);
        stopwatch.Stop();
        if (index == WaitHandle.WaitTimeout)
          throw new TimeoutException("Timeout when reading from shared memory");

        if (waitHandles[index] == connectionClosed)
          throw new MySqlException("Connection to server lost", true, null);

        if (readTimeout != System.Threading.Timeout.Infinite)
        {
          timeLeft = readTimeout - (int)stopwatch.ElapsedMilliseconds;
          if (timeLeft < 0)
            throw new TimeoutException("Timeout when reading from shared memory");
        }

        bytesLeft = Marshal.ReadInt32(data.View);
        position = 4;
      }

      int len = Math.Min(count, bytesLeft);
      long baseMem = data.View.ToInt64() + position;

      for (int i = 0; i < len; i++, position++)
        buffer[offset + i] = Marshal.ReadByte((IntPtr)(baseMem + i));

      bytesLeft -= len;
      if (bytesLeft == 0)
        clientRead.Set();

      return len;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
      throw new NotSupportedException("SharedMemoryStream does not support seeking");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
      int leftToDo = count;
      int buffPos = offset;
      WaitHandle[] waitHandles = { serverRead, connectionClosed };
      LowResolutionStopwatch stopwatch = new LowResolutionStopwatch();
      int timeLeft = writeTimeout;

      while (leftToDo > 0)
      {
        stopwatch.Start();
        int index = WaitHandle.WaitAny(waitHandles, timeLeft);
        stopwatch.Stop();

        if (waitHandles[index] == connectionClosed)
          throw new MySqlException("Connection to server lost", true, null);

        if (index == WaitHandle.WaitTimeout)
          throw new TimeoutException("Timeout when reading from shared memory");

        if (writeTimeout != System.Threading.Timeout.Infinite)
        {
          timeLeft = writeTimeout - (int)stopwatch.ElapsedMilliseconds;
          if (timeLeft < 0)
            throw new TimeoutException("Timeout when writing to shared memory");
        }
        int bytesToDo = Math.Min(leftToDo, BUFFERLENGTH);
        long baseMem = data.View.ToInt64() + 4;
        Marshal.WriteInt32(data.View, bytesToDo);
        Marshal.Copy(buffer, buffPos, (IntPtr)baseMem, bytesToDo);
        buffPos += bytesToDo;
        leftToDo -= bytesToDo;
        if (!clientWrote.Set())
          throw new MySqlException("Writing to shared memory failed");
      }
    }

    public override void SetLength(long value)
    {
      throw new NotSupportedException("SharedMemoryStream does not support seeking");
    }

    public override bool CanTimeout
    {
      get
      {
        return true;
      }
    }

    public override int ReadTimeout
    {
      get
      {
        return readTimeout;
      }
      set
      {
        readTimeout = value;
      }
    }

    public override int WriteTimeout
    {
      get
      {
        return writeTimeout;
      }
      set
      {
        writeTimeout = value;
      }
    }

  }
#endif
}
#endif
