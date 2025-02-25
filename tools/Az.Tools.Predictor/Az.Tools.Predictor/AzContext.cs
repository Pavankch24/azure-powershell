// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Azure.PowerShell.Tools.AzPredictor
{
    using PowerShell = System.Management.Automation.PowerShell;

    /// <summary>
    /// The class for the current Azure PowerShell context.
    /// </summary>
    internal sealed class AzContext : IAzContext, IDisposable
    {
        private const string InternalUserSuffix = "@microsoft.com";
        private static readonly Version DefaultVersion = new Version("0.0.0.0");

        private PowerShell _powerShellRuntime;
        private PowerShell PowerShellRuntime
        {
            get
            {
                if (_powerShellRuntime == null)
                {
                    _powerShellRuntime = PowerShell.Create(DefaultRunspace);
                }

                return _powerShellRuntime;
            }
        }

        private readonly Lazy<Runspace> _defaultRunspace = new(() =>
                {
                    // Create a mini runspace by remove the types and formats
                    InitialSessionState minimalState = InitialSessionState.CreateDefault2();
                    minimalState.Types.Clear();
                    minimalState.Formats.Clear();
                    // Refer to the remarks for the property DefaultRunspace.
                    var runspace = RunspaceFactory.CreateRunspace(minimalState);
                    runspace.Open();
                    return runspace;
                });

        /// <inheritdoc />
        /// <remarks>
        /// We don't pre-load Az service modules since they may not always be installed.
        /// Creating the instance is at the first time this is called.
        /// It can be slow. So the first call must not be in the path of the user interaction.
        /// Loading too many modules can also impact user experience because that may add to much memory pressure at the same
        /// time.
        /// </remarks>
        public Runspace DefaultRunspace => _defaultRunspace.Value;

        /// <inheritdoc/>
        public Version AzVersion { get; private set; } = DefaultVersion;

        private int? _cohort;
        /// <inheritdoc/>
        public int Cohort
        {
            get
            {
                if (!_cohort.HasValue)
                {
                    if (!string.IsNullOrWhiteSpace(MacAddress))
                    {
                        if (int.TryParse($"{MacAddress.Last()}", NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int lastDigit))
                        {
                            _cohort = lastDigit % AzPredictorConstants.CohortCount;
                            return _cohort.Value;
                        }
                    }

                    _cohort = (new Random(DateTime.UtcNow.Millisecond)).Next() % AzPredictorConstants.CohortCount;
                }

                return _cohort.Value;
            }
        }

        /// <inheritdoc/>
        public string HashUserId { get; private set; } = string.Empty;

        private string _macAddress;
        /// <inheritdoc/>
        public string MacAddress
        {
            get
            {
                if (_macAddress == null)
                {
                    _macAddress = string.Empty;

                    var macAddress = GetMACAddress();
                    if (!string.IsNullOrWhiteSpace(macAddress))
                    {
                        _macAddress = GenerateSha256HashString(macAddress)?.Replace("-", string.Empty).ToLowerInvariant();
                    }
                }

                return _macAddress;
            }
        }

        /// <inheritdoc/>
        public string OSVersion
        {
            get
            {
                return Environment.OSVersion.ToString();
            }
        }

        private Version _powerShellVersion;
        /// <inheritdoc/>
        public Version PowerShellVersion
        {
            get
            {
                if (_powerShellVersion == null)
                {
                    var outputs = ExecuteScript<Version>("(Get-Host).Version");

                    _powerShellVersion = outputs.FirstOrDefault();
                }

                return _powerShellVersion ?? AzContext.DefaultVersion;
            }
        }

        private Version _moduleVersion;
        /// <inheritdoc/>
        public Version ModuleVersion
        {
            get
            {
                if (_moduleVersion == null)
                {
                    _moduleVersion = this.GetType().Assembly.GetName().Version;
                }

                return _moduleVersion ?? AzContext.DefaultVersion;
            }
        }

        /// <inheritdoc/>
        public bool IsInternal { get; internal set; }

        /// <inheritdoc/>
        public void UpdateContext()
        {
            AzVersion = GetAzVersion();
            RawUserId = GetUserAccountId();
            HashUserId = GenerateSha256HashString(RawUserId);

            if (!IsInternal)
            {
                IsInternal = RawUserId.EndsWith(AzContext.InternalUserSuffix, StringComparison.OrdinalIgnoreCase);
            }
        }

        public void Dispose()
        {
            if (_powerShellRuntime != null)
            {
                _powerShellRuntime.Dispose();
                _powerShellRuntime = null;
            }

            if (_defaultRunspace.IsValueCreated)
            {
                _defaultRunspace.Value.Dispose();
            }
        }

        internal string RawUserId { get; set; }

        /// <summary>
        /// Gets the user account id if the user logs in, otherwise empty string.
        /// </summary>
        private string GetUserAccountId()
        {
            try
            {
                var output = ExecuteScript<string>("(Get-AzContext).Account.Id");
                return output.FirstOrDefault() ?? string.Empty;
            }
            catch (Exception)
            {
            }

            return string.Empty;
        }

        /// <summary>
        /// Gets the latest version from the loaded Az modules.
        /// </summary>
        private Version GetAzVersion()
        {
            Version latestAzVersion = DefaultVersion;

            try
            {
                var outputs = ExecuteScript<PSObject>("Get-Module -Name Az -ListAvailable");

                if (!(outputs?.Any() == true))
                {
                    outputs = ExecuteScript<PSObject>("Get-Module -Name AzPreview -ListAvailable");
                }

                if (outputs?.Any() == true)
                {
                    ExtractAndSetLatestAzVersion(outputs);
                }
            }
            catch (Exception)
            {
            }

            return latestAzVersion;

            void ExtractAndSetLatestAzVersion(IEnumerable<PSObject> outputs)
            {
                foreach (var psObject in outputs)
                {
                    string versionOutput = psObject.Properties["Version"].Value.ToString();
                    int positionOfVersion = versionOutput.IndexOf('-');
                    Version currentAzVersion = (positionOfVersion == -1) ? new Version(versionOutput) : new Version(versionOutput.Substring(0, positionOfVersion));
                    if (currentAzVersion > latestAzVersion)
                    {
                        latestAzVersion = currentAzVersion;
                    }
                }
            }
        }

        /// <summary>
        /// Executes the PowerShell cmdlet in the current powershell session.
        /// </summary>
        private List<T> ExecuteScript<T>(string contents)
        {
            List<T> output = new List<T>();

            PowerShellRuntime.Commands.Clear();
            PowerShellRuntime.AddScript(contents);
            Collection<T> result = PowerShellRuntime.Invoke<T>();

            if (result != null && result.Count > 0)
            {
                output.AddRange(result);
            }

            return output;
        }

        /// <summary>
        /// Generate a SHA256 Hash string from the originInput.
        /// </summary>
        /// <param name="originInput"></param>
        /// <returns>The Sha256 hash, or empty if the input is only whitespace</returns>
        private static string GenerateSha256HashString(string originInput)
        {
            if (string.IsNullOrWhiteSpace(originInput))
            {
                return string.Empty;
            }

            string result = string.Empty;
            try
            {
                using (var sha256 = SHA256.Create())
                {
                    var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(originInput));
                    result = BitConverter.ToString(bytes);
                }
            }
            catch
            {
                // do not throw if CryptoProvider is not provided
            }

            return result;
        }

        /// <summary>
        /// Get the MAC address of the default NIC, or null if none can be found.
        /// </summary>
        /// <returns>The MAC address of the defautl nic, or null if none is found.</returns>
        private static string GetMACAddress()
        {
            return NetworkInterface.GetAllNetworkInterfaces()?
                                    .FirstOrDefault(nic => nic != null &&
                                                           nic.OperationalStatus == OperationalStatus.Up &&
                                                           !string.IsNullOrWhiteSpace(nic.GetPhysicalAddress()?.ToString()))?
                                    .GetPhysicalAddress()?.ToString();
        }
    }
}
