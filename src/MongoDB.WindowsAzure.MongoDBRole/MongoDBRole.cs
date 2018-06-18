﻿/*
 * Copyright 2010-2013 10gen Inc.
 * file : MongoDBRole.cs
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 * 
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace MongoDB.WindowsAzure.MongoDBRole
{

    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading;

    using Microsoft.WindowsAzure;
    using Microsoft.WindowsAzure.ServiceRuntime;
    using Microsoft.WindowsAzure.StorageClient;
    
    using MongoDB.Driver;
    using MongoDB.WindowsAzure.Common;

    public class MongoDBRole : RoleEntryPoint
    {

        private Process mongodProcess = null;
        private CloudDrive mongoDataDrive = null;
        private string mongodHost;
        private int mongodPort;
        private string mongodDataDriveLetter = null;
        private string replicaSetName = null;
        private int instanceId;
        private TimeSpan runSleepInterval = new TimeSpan(0, 0, 15);
        private int replicaSetRoleCount;
        private bool replicaSetInitialized = false;

        public override void Run()
        {
            DiagnosticsHelper.TraceInformation("MongoWorkerRole run method called");
            var mongodRunning = CheckIfMongodRunning();

            while (mongodRunning || !Settings.RecycleRoleOnExit)
            {
                Thread.Sleep(runSleepInterval);
                mongodRunning = CheckIfMongodRunning();
            }

            DiagnosticsHelper.TraceWarning("MongoWorkerRole run method exiting");
        }

        public override bool OnStart()
        {
            DiagnosticsHelper.TraceInformation("MongoWorkerRole onstart called");

            // For information on handling configuration changes
            // see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.

            // Set the maximum number of concurrent connections 
            ServicePointManager.DefaultConnectionLimit = 12;

            CloudStorageAccount.SetConfigurationSettingPublisher((configName, configSetter) =>
            {
                configSetter(RoleEnvironment.GetConfigurationSettingValue(configName));
            });

            RoleEnvironment.Changing += RoleEnvironmentChanging;
            RoleEnvironment.Changed += RoleEnvironmentChanged;

            replicaSetName = RoleEnvironment.GetConfigurationSettingValue(Constants.ReplicaSetNameSetting);
            instanceId = ConnectionUtilities.ParseNodeInstanceId(RoleEnvironment.CurrentRoleInstance.Id);

            DiagnosticsHelper.TraceInformation("ReplicaSetName={0}, InstanceId={1}",
                replicaSetName, instanceId);

            SetHostAndPort();
            DiagnosticsHelper.TraceInformation("Obtained host={0}, port={1}", mongodHost, mongodPort);

            StartMongoD();
            DiagnosticsHelper.TraceInformation("Mongod process started");

            // Need to ensure MongoD is up here
            DatabaseHelper.EnsureMongodIsListening(replicaSetName, instanceId, mongodPort);

            if (instanceId == 0)
            {
                if (!DatabaseHelper.IsReplicaSetInitialized(mongodPort))
                {
                    DiagnosticsHelper.TraceInformation("RSInit not initialized");
                    try
                    {
                        replicaSetRoleCount = DatabaseHelper.RunInitializeCommandLocally(replicaSetName, mongodPort);
                        replicaSetInitialized = true;
                        DiagnosticsHelper.TraceInformation("RSInit issued successfully");
                    }
                    catch (MongoCommandException e)
                    {
                        //Ignore exceptions caught on rs init for now
                        DiagnosticsHelper.TraceWarning(
                            "Exception {0} on RSInit with {1}",
                            e.Message, e.StackTrace);
                    }
                }
                else
                {
                    replicaSetInitialized = true;
                    replicaSetRoleCount = DatabaseHelper.GetReplicaSetMemberCount(mongodPort);
                    DiagnosticsHelper.TraceInformation("RSInit already initialized with {0} instances", replicaSetRoleCount);
                    var currentRoleCount = RoleEnvironment.Roles[RoleEnvironment.CurrentRoleInstance.Role.Name].Instances.Count;
                    DiagnosticsHelper.TraceInformation("Need reconfig current={0}, new={1}", replicaSetRoleCount, currentRoleCount);
                    if (replicaSetRoleCount != currentRoleCount)
                    {
                        replicaSetRoleCount = DatabaseHelper.ReconfigReplicaSet(replicaSetName, mongodPort);
                        DiagnosticsHelper.TraceInformation("RS reconfig succeeded. New role count {0}", replicaSetRoleCount);
                    }
                }
            }

            DiagnosticsHelper.TraceInformation("Done with OnStart");
            return true;
        }

        public override void OnStop()
        {
            DiagnosticsHelper.TraceInformation("MongoWorkerRole onstop called");
            try
            {
                // should we instead call Process.stop?
                if ((mongodProcess != null) &&
                    !(mongodProcess.HasExited))
                {
                    DiagnosticsHelper.TraceInformation("Stepdown called on mongod");
                    DatabaseHelper.StepdownIfNeeded(mongodPort);
                }
            }
            catch (Exception e)
            {
                //Ignore any and all exceptions here since we want the rest
                // of the cleanup actions to happen
                DiagnosticsHelper.TraceWarning(
                    "Exception in onstop - stepdown failed with {0}, {1}",
                    e.Message, e.StackTrace);
            }

            try
            {
                // should we instead call Process.stop?
                if ((mongodProcess != null) &&
                    !(mongodProcess.HasExited))
                {
                    DiagnosticsHelper.TraceInformation("Shutdown called on mongod");
                    DatabaseHelper.ShutdownMongo(mongodPort);
                }
                DiagnosticsHelper.TraceInformation("Shutdown completed on mongod");
            }
            catch (Exception e)
            {
                //Ignore any and all exceptions here since we want the rest
                // of the cleanup actions to happen
                DiagnosticsHelper.TraceWarning(
                    "Exception in onstop - shutdown failed with {0} {1}",
                    e.Message, e.StackTrace);
            }

            try
            {
                if (mongoDataDrive != null)
                {
                    DiagnosticsHelper.TraceInformation("Unmount called on data drive");
                    mongoDataDrive.Unmount();
                }
                DiagnosticsHelper.TraceInformation("Unmount completed on data drive");
            }
            catch (Exception e)
            {
                //Ignore any and all exceptions here
                DiagnosticsHelper.TraceWarning(
                    "Exception in onstop - unmount failed with {0} {1}", 
                    e.Message, e.StackTrace);
            }

        }

        private void SetHostAndPort()
        {
            var endPoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints[Constants.MongodPortSetting].IPEndpoint;
            mongodHost = endPoint.Address.ToString();
            mongodPort = endPoint.Port;
            if (RoleEnvironment.IsEmulated)
            {
                mongodPort += instanceId;
            }
        }

        private void StartMongoD()
        {
            var mongoAppRoot = Path.Combine(
                Environment.GetEnvironmentVariable("RoleRoot") + @"\",
                Settings.MongoDBBinaryFolder);
            var mongodPath = Path.Combine(mongoAppRoot, @"mongod.exe");

            var blobPath = GetMongoDataDirectory();

            var logFile = GetLogFile();

            var logLevel = Settings.MongodLogLevel;

            string cmdline;
            if (RoleEnvironment.IsEmulated)
            {
                cmdline = String.Format(Settings.MongodCommandLineEmulated,
                    mongodPort,
                    blobPath,
                    logFile,
                    replicaSetName,
                    logLevel);
            }
            else
            {
                cmdline = String.Format(Settings.MongodCommandLineCloud,
                    mongodPort,
                    blobPath,
                    logFile,
                    replicaSetName,
                    logLevel);
            }

            DiagnosticsHelper.TraceInformation("Launching mongod as {0} {1}", 
                mongodPath, cmdline);

            // launch mongo
            try
            {
                mongodProcess = new Process()
                {
                    StartInfo = new ProcessStartInfo(mongodPath, cmdline)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = mongoAppRoot,
                        CreateNoWindow = false
                    }
                };
                mongodProcess.Start();
            }
            catch (Exception e)
            {
                // Catching exception purely to log it
                DiagnosticsHelper.TraceError("Can't start Mongo: " + e.Message);
                // throwing an exception here causes the VM to recycle
                throw new ApplicationException("Can't start mongo: " + e.Message); 
            }
        }

        private string GetMongoDataDirectory()
        {
            DiagnosticsHelper.TraceInformation("Getting db path");
            var dataBlobName = string.Format(Constants.MongoDataBlobName, instanceId);
            var containerName = ConnectionUtilities.GetDataContainerName(replicaSetName);
            mongodDataDriveLetter = Utilities.GetMountedPathFromBlob(
                Settings.LocalCacheDirSetting,
                Constants.MongoDataCredentialSetting,
                containerName,
                dataBlobName,
                Settings.MaxDBDriveSizeInMB,
                out mongoDataDrive);
            DiagnosticsHelper.TraceInformation("Obtained data drive as {0}", mongodDataDriveLetter);
            var dir = Directory.CreateDirectory(Path.Combine(mongodDataDriveLetter, @"data"));
            DiagnosticsHelper.TraceInformation("Data directory is {0}", dir.FullName);
            return dir.FullName;
        }

        private string GetLogFile()
        {
            DiagnosticsHelper.TraceInformation("Getting log file base path");
            var localStorage = RoleEnvironment.GetLocalResource(Settings.LogDirSetting);
            var logfile = Path.Combine(localStorage.RootPath + @"\", Settings.MongodLogFileName);
            return ("\"" + logfile + "\"");
        }

        private bool CheckIfMongodRunning()
        {
            var processExited = mongodProcess.HasExited;
            return !processExited;
        }

        private void RoleEnvironmentChanging(object sender, RoleEnvironmentChangingEventArgs e)
        {
            Func<RoleEnvironmentConfigurationSettingChange, bool> changeIsExempt =
                x => !Settings.ExemptConfigurationItems.Contains(x.ConfigurationSettingName);
            var environmentChanges = e.Changes.OfType<RoleEnvironmentConfigurationSettingChange>();
            e.Cancel = environmentChanges.Any(changeIsExempt);
            DiagnosticsHelper.TraceInformation("Role config changing. Cancel set to {0}",
                e.Cancel);
        }

        private void RoleEnvironmentChanged(object sender, RoleEnvironmentChangedEventArgs e)
        {
            // Get the list of configuration changes
            var settingChanges = e.Changes.OfType<RoleEnvironmentConfigurationSettingChange>();

            foreach (var settingChange in settingChanges)
            {
                var settingName = settingChange.ConfigurationSettingName;
                var value = RoleEnvironment.GetConfigurationSettingValue(settingName);
                DiagnosticsHelper.TraceInformation(
                    "Setting {0} now has value {1} ",
                    settingName,
                    value);
                if (settingName == Settings.LogVerbositySetting)
                {
                    var logLevel = Utilities.GetLogVerbosity(value);
                    if (logLevel != null)
                    {
                        if (logLevel != Settings.MongodLogLevel)
                        {
                            Settings.MongodLogLevel = logLevel;
                            DatabaseHelper.SetLogLevel(mongodPort, logLevel);
                        }
                    }
                }
                if (settingName == Settings.RecycleSetting)
                {
                    Settings.RecycleRoleOnExit = Utilities.GetRecycleFlag(RoleEnvironment.GetConfigurationSettingValue(settingName));
                }
            }


            // Get the list of topology changes
            var topologyChanges = e.Changes.OfType<RoleEnvironmentTopologyChange>();

            foreach (var topologyChange in topologyChanges)
            {
                var roleName = topologyChange.RoleName;
                var roleCount = RoleEnvironment.Roles[roleName].Instances.Count;
                DiagnosticsHelper.TraceInformation(
                    "Role {0} now has {1} instance(s)",
                    roleName,
                    roleCount);
                if (instanceId == 0 && roleName.Equals(Constants.MongoDBWorkerRoleName))
                {
                    DiagnosticsHelper.TraceInformation(
                        "{0} instance count changed from {1} {2}",
                        roleName,
                        replicaSetRoleCount,
                        roleCount);
                    if (replicaSetRoleCount != roleCount)
                    {
                        if (replicaSetInitialized)
                        {
                            replicaSetRoleCount = DatabaseHelper.ReconfigReplicaSet(replicaSetName, mongodPort);
                            DiagnosticsHelper.TraceInformation("RS reconfig succeeded. New role count {0}", replicaSetRoleCount);
                        }
                        else
                        {
                            // config changed even before rs init
                            DiagnosticsHelper.TraceWarning("Role count change before rs init.");
                        }
                    }
                }
            }
        }

    }
}
