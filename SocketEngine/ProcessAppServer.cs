﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Config;
using SuperSocket.SocketBase.Metadata;
using SuperSocket.SocketBase.Provider;

namespace SuperSocket.SocketEngine
{
    [StatusInfo(StatusInfoKeys.CpuUsage, Name = "CPU Usage", Format = "{0:0.00}%", DataType = typeof(double), Order = 112)]
    [StatusInfo(StatusInfoKeys.MemoryUsage, Name = "Physical Memory Usage", Format = "{0:N}", DataType = typeof(double), Order = 113)]
    [StatusInfo(StatusInfoKeys.TotalThreadCount, Name = "Total Thread Count", Format = "{0}", DataType = typeof(double), Order = 114)]
    [StatusInfo(StatusInfoKeys.AvailableWorkingThreads, Name = "Available Working Threads", Format = "{0}", DataType = typeof(double), Order = 512)]
    [StatusInfo(StatusInfoKeys.AvailableCompletionPortThreads, Name = "Available Completion Port Threads", Format = "{0}", DataType = typeof(double), Order = 513)]
    [StatusInfo(StatusInfoKeys.MaxWorkingThreads, Name = "Maximum Working Threads", Format = "{0}", DataType = typeof(double), Order = 513)]
    [StatusInfo(StatusInfoKeys.MaxCompletionPortThreads, Name = "Maximum Completion Port Threads", Format = "{0}", DataType = typeof(double), Order = 514)]
    partial class ProcessAppServer : IsolationAppServer
    {
        private const string m_AgentUri = "ipc://{0}/WorkItemAgent.rem";

        private const string m_PortNameTemplate = "{0}[SuperSocket.Agent:{1}]";

        private Process m_WorkingProcess;

        private string m_ServerTag;

        private ProcessLocker m_Locker;

        private AutoResetEvent m_ProcessWorkEvent = new AutoResetEvent(false);

        private string m_ProcessWorkStatus = string.Empty;

        private ProcessPerformanceCounterHelper m_PerformanceCounterHelper;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProcessAppServer" /> class.
        /// </summary>
        /// <param name="serverTypeName">Name of the server type.</param>
        /// <param name="serverStatusMetadata">The server status metadata.</param>
        public ProcessAppServer(string serverTypeName, StatusInfoAttribute[] serverStatusMetadata)
            : base(serverTypeName, serverStatusMetadata)
        {

        }

        protected override IWorkItemBase Start()
        {
            var currentDomain = AppDomain.CurrentDomain;
            var workingDir = Path.Combine(Path.Combine(currentDomain.BaseDirectory, WorkingDir), Name);

            if (!Directory.Exists(workingDir))
                Directory.CreateDirectory(workingDir);

            m_Locker = new ProcessLocker(workingDir, "instance.lock");

            var portName = string.Format(m_PortNameTemplate, Name, "{0}");

            var process = m_Locker.GetLockedProcess();


            if (process == null)
            {
                var args = new string[] { Name, portName, workingDir };

                var startInfo = new ProcessStartInfo("SuperSocket.Agent.exe", string.Join(" ", args.Select(a => "\"" + a + "\"").ToArray()));
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.WorkingDirectory = currentDomain.BaseDirectory;
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                try
                {
                    m_WorkingProcess = Process.Start(startInfo);
                    m_WorkingProcess.ErrorDataReceived += new DataReceivedEventHandler(m_WorkingProcess_ErrorDataReceived);
                    m_WorkingProcess.BeginErrorReadLine();
                    m_WorkingProcess.OutputDataReceived += new DataReceivedEventHandler(m_WorkingProcess_OutputDataReceived);
                    m_WorkingProcess.BeginOutputReadLine();
                }
                catch (Exception e)
                {
                    OnExceptionThrown(e);
                    return null;
                }
            }
            else
            {
                m_WorkingProcess = process;
            }

            portName = string.Format(portName, m_WorkingProcess.Id);
            m_ServerTag = portName;

            var remoteUri = string.Format(m_AgentUri, portName);
            var appServer = (IRemoteWorkItem)Activator.GetObject(typeof(IRemoteWorkItem), remoteUri);

            if (process == null)
            {
                if (!m_ProcessWorkEvent.WaitOne(5000))
                {
                    ShutdownProcess();
                    OnExceptionThrown(new Exception("The remote work item was timeout to setup!"));
                    return null;
                }

                if (!"Ok".Equals(m_ProcessWorkStatus, StringComparison.OrdinalIgnoreCase))
                {
                    OnExceptionThrown(new Exception("The Agent process didn't start successfully!"));
                    return null;
                }

                var bootstrapIpcPort = AppDomain.CurrentDomain.GetData("BootstrapIpcPort") as string;

                if (string.IsNullOrEmpty(bootstrapIpcPort))
                    throw new Exception("The bootstrap's remoting service has not been started.");

                //Setup and then start the remote server instance
                var ret = appServer.Setup(ServerTypeName, "ipc://" + bootstrapIpcPort + "/Bootstrap.rem", currentDomain.BaseDirectory, ServerConfig, Factories);

                if (!ret)
                {
                    ShutdownProcess();
                    OnExceptionThrown(new Exception("The remote work item failed to setup!"));
                    return null;
                }

                ret = appServer.Start();

                if (!ret)
                {
                    ShutdownProcess();
                    OnExceptionThrown(new Exception("The remote work item failed to start!"));
                    return null;
                }

                m_Locker.SaveLock(m_WorkingProcess);
            }

            m_WorkingProcess.EnableRaisingEvents = true;
            m_WorkingProcess.Exited += new EventHandler(m_WorkingProcess_Exited);

            m_PerformanceCounterHelper = new ProcessPerformanceCounterHelper(m_WorkingProcess);

            return appServer;
        }

        void m_WorkingProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            if (string.IsNullOrEmpty(m_ProcessWorkStatus))
            {
                m_ProcessWorkStatus = e.Data;
                m_ProcessWorkEvent.Set();
                return;
            }

            Console.WriteLine(string.Format("{0}: {1}", m_ServerTag, e.Data));
        }

        void m_WorkingProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            OnExceptionThrown(new Exception(e.Data));
        }

        void m_WorkingProcess_Exited(object sender, EventArgs e)
        {
            m_PerformanceCounterHelper = null;
            m_Locker.CleanLock();
            OnStopped();
        }

        protected override void OnStopped()
        {
            var unexpectedShutdown = (State == ServerState.Running);

            base.OnStopped();
            m_WorkingProcess = null;
            m_ProcessWorkStatus = string.Empty;

            if (unexpectedShutdown)
            {
                //auto restart if meet a unexpected shutdown
                ((IWorkItemBase)this).Start();
            }
        }

        private void ShutdownProcess()
        {
            if (m_WorkingProcess != null)
            {
                try
                {
                    m_WorkingProcess.Kill();
                }
                catch
                {

                }
            }
        }

        protected override void Stop()
        {
            ShutdownProcess();
        }

        protected override bool StatusMetadataExtended
        {
            get
            {
                return true;
            }
        }

        public override StatusInfoCollection CollectServerStatus(StatusInfoCollection nodeStatus)
        {
            var status = base.CollectServerStatus(nodeStatus);
            status.Tag = m_ServerTag;

            if(m_PerformanceCounterHelper != null)
                m_PerformanceCounterHelper.Collect(status);

            return status;
        }
    }
}