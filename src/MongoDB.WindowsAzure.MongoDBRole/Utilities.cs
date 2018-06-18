﻿/*
 * Copyright 2010-2013 10gen Inc.
 * file : Utilities.cs
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
    using System.Text.RegularExpressions;

    using Microsoft.WindowsAzure;
    using Microsoft.WindowsAzure.ServiceRuntime;
    using Microsoft.WindowsAzure.StorageClient;

    internal static class Utilities
    {

        private static readonly Regex logLevelRegex = new Regex("^(-?)([v]*)$");
        private static string currentRoleName = null;

        static Utilities()
        {
            currentRoleName = RoleEnvironment.CurrentRoleInstance.Role.Name;
        }

        internal static string GetMountedPathFromBlob(
            string localCachePath,
            string cloudDir,
            string containerName,
            string blobName,
            int driveSize,
            out CloudDrive mongoDrive)
        {

            DiagnosticsHelper.TraceInformation(
                "In mounting cloud drive for dir {0} on {1} with {2}",
                cloudDir,
                containerName,
                blobName);

            CloudStorageAccount storageAccount = CloudStorageAccount.FromConfigurationSetting(cloudDir);
            
            var blobClient = storageAccount.CreateCloudBlobClient();

            DiagnosticsHelper.TraceInformation("Get container");
            // this should be the name of your replset
            var driveContainer = blobClient.GetContainerReference(containerName);

            // create blob container (it has to exist before creating the cloud drive)
            try
            {
                driveContainer.CreateIfNotExist();
            }
            catch (StorageException e)
            {
                DiagnosticsHelper.TraceInformation(
                    "Container creation failed with {0} {1}",
                    e.Message, 
                    e.StackTrace);
            }

            var mongoBlobUri = blobClient.GetContainerReference(containerName).GetPageBlobReference(blobName).Uri.ToString();
            DiagnosticsHelper.TraceInformation("Blob uri obtained {0}", mongoBlobUri);

            // create the cloud drive
            mongoDrive = storageAccount.CreateCloudDrive(mongoBlobUri);
            try
            {
                mongoDrive.CreateIfNotExist(driveSize);
            }
            catch (CloudDriveException e)
            {
                DiagnosticsHelper.TraceInformation(
                    "Drive creation failed with {0} {1}",
                    e.Message, 
                    e.StackTrace);

            }

            DiagnosticsHelper.TraceInformation("Initialize cache");
            var localStorage = RoleEnvironment.GetLocalResource(localCachePath);

            CloudDrive.InitializeCache(localStorage.RootPath.TrimEnd('\\'),
                localStorage.MaximumSizeInMegabytes);

            // mount the drive and get the root path of the drive it's mounted as
            try
            {
                DiagnosticsHelper.TraceInformation(
                    "Trying to mount blob as azure drive");
                var driveLetter = mongoDrive.Mount(localStorage.MaximumSizeInMegabytes,
                    DriveMountOptions.None);
                DiagnosticsHelper.TraceInformation(
                    "Write lock acquired on azure drive, mounted as {0}",
                    driveLetter);
                return driveLetter;
            }
            catch (CloudDriveException e)
            {
                DiagnosticsHelper.TraceCritical(
                    "Failed to mount cloud drive with {0} {1}",
                    e.Message, 
                    e.StackTrace);
                throw;
            }
        }

        internal static string GetLogVerbosity(string configuredLogLevel)
        {
            string logLevel = null;
            if (!string.IsNullOrEmpty(configuredLogLevel))
            {
                Match m = logLevelRegex.Match(configuredLogLevel);
                if (m.Success)
                {
                    logLevel = string.IsNullOrEmpty(m.Groups[1].ToString()) ?
                        "-" + m.Groups[0].ToString() :
                        m.Groups[0].ToString();
                }

            }
            return logLevel;
        }

        internal static bool GetRecycleFlag(string configuredRecycle)
        {
            bool recycle = false;
            if ("true".CompareTo(configuredRecycle.ToLowerInvariant()) == 0)
            {
                recycle = true;
            }
            return recycle;
        }

    }

}
