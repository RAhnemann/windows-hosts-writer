using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace windows_hosts_writer
{
    class Program
    {
        //Settings keys
        private const string ENV_ENDPOINT = "endpoint";
        private const string ENV_NETWORK = "network";
        private const string ENV_HOSTPATH = "hosts_path";
        private const string ENV_SESSION = "session_id";
        private const string ENV_TERMMAP = "termination_map";

        private const string CONST_ANYNET = "ANY_NETWORK";

        //Client used to read the container details
        private static DockerClient _client;

        //Listening to a specific network
        private static string _listenNetwork = CONST_ANYNET;

        //Location of the hosts file IN the container.  Mapped through a volume share to your hosts file
        private static string _hostsPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "c:\\driversetc\\hosts"
            : "/host-etc/hosts";

        //All host file entries we're tracking
        private static Dictionary<string, List<string>> _hostsEntries = new Dictionary<string, List<string>>();

        //Current Hash of the HostsEntries, stops the spammy updates to hosts file
        private static int _hostsHash = -1;

        //Uniquely identify records created by this. Allows for simultaneous execution
        private static string _sessionId = "";

        //Our time used for sync
        private static System.Timers.Timer _timer;

        private static Dictionary<string, string> _termMaps = new Dictionary<string, string>();
        private static int _timerPeriod = 10000;


        //  Due to how windows container handle the terminate events in windows, there's
        //  not a clear-cut way to handle graceful exists.  Having to resort to this stuff
        //  isn't ideal, but the standard approaches of AppDomain.CurrentDomain.ProcessExit
        //  and AssemblyLoadContext.Default.Unloading don't handle the events correctly
        [DllImport("Kernel32")]
        internal static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool Add);

        internal delegate bool HandlerRoutine(CtrlTypes ctrlType);

        internal enum CtrlTypes
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT,
            CTRL_CLOSE_EVENT,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT
        }

        static void Main(string[] args)
        {
            Log("Starting Hosts Writer");

            if (Environment.GetEnvironmentVariable(ENV_HOSTPATH) != null)
            {
                _hostsPath = Environment.GetEnvironmentVariable(ENV_HOSTPATH);
                Log($"Overriding hosts path '{_hostsPath}'");
            }

            if (Environment.GetEnvironmentVariable(ENV_NETWORK) != null)
            {
                _listenNetwork = Environment.GetEnvironmentVariable(ENV_NETWORK);
                Log($"Overriding listen network '{_listenNetwork}'");
            }
            else
            {
                Log("Listening to any network");
            }

            if (Environment.GetEnvironmentVariable(ENV_SESSION) != null)
            {
                _sessionId = Environment.GetEnvironmentVariable(ENV_SESSION);
                Log($"Overriding Session Key  '{_sessionId}'");
            }

            if (Environment.GetEnvironmentVariable(ENV_TERMMAP) != null)
            {
                var mapValue = Environment.GetEnvironmentVariable(ENV_TERMMAP);
                if (string.IsNullOrEmpty(mapValue))
                {
                    Log($"No termination maps detected");
                }
                else
                {
                    var mapSets = mapValue.Split('|');

                    foreach (var mapGroup in mapSets)
                    {
                        var mapSet = mapGroup.Split(":");

                        if (mapSet.Length != 2)
                        {
                            Log($"Malformed MapSet: '{ mapGroup}'. Expected 'source1,source2:dest'.");
                            continue;
                        }

                        var mapDest = mapSet[1].Trim();

                        foreach (string mapSource in mapSet[0].Split(",", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList())
                        {
                            if (_termMaps.ContainsKey(mapSource))
                            {
                                Log($"Skipping Duplicate Source Map '{mapSource}'");
                                continue;
                            }
                            Log($"Mapping {mapSource.ToLower()} to {mapDest.ToLower()}");
                            _termMaps.Add(mapSource.ToLower(), mapDest.ToLower());
                        }
                    }

                    Log($"Configured {_termMaps.Count} termination map{(_termMaps.Count == 1 ? "" : "s")}");
                }
            }

            // Fail fast if we can't write to the hosts file. Otherwise the timer loop
            // silently swallows the access error and nothing ever appears in the file.
            try
            {
                using (File.Open(_hostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                }
            }
            catch (FileNotFoundException)
            {
                Log($"FATAL: Hosts file not found at '{_hostsPath}'.");
                Log("Check that the volume mount points at a real hosts file.");
                Environment.Exit(1);
            }
            catch (DirectoryNotFoundException)
            {
                Log($"FATAL: Hosts file directory not found for '{_hostsPath}'.");
                Log("Check that the volume mount path is correct.");
                Environment.Exit(1);
            }
            catch (UnauthorizedAccessException)
            {
                Log($"FATAL: No write access to hosts file at '{_hostsPath}'.");
                Log("The container cannot modify the mounted hosts file. Verify the volume mount and that the container user can write to the file.");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Log($"FATAL: Could not access hosts file at '{_hostsPath}': {ex.Message}");
                Environment.Exit(1);
            }

            try
            {
                _client = GetClient();
            }
            catch (Exception ex)
            {
                Log($"Something went wrong. Likely the Docker engine is not listening at [{_client.Configuration.EndpointBaseUri}] inside of the container.");
                Log($"You can change that path through environment variable '{ENV_ENDPOINT}'");

                Log("Exception is " + ex.Message);
                Log(ex.StackTrace);

                if (ex.InnerException != null)
                {
                    Log("InnerException is " + ex.InnerException.Message);
                    Log(ex.InnerException.StackTrace);
                }

                //Exit Gracefully
                return;
            }

            try
            {
                Log("Scheduling container watcher");
                _timer = new System.Timers.Timer(_timerPeriod);
                _timer.Elapsed += (s, e) => { DoUpdate(); };
                _timer.Start();

                var shutdown = new ManualResetEvent(false);
                var complete = new ManualResetEventSlim();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var hr = new HandlerRoutine(type =>
                    {
                        Log($"ConsoleCtrlHandler got signal: {type}");

                        shutdown.Set();
                        complete.Wait();

                        return false;
                    });

                    SetConsoleCtrlHandler(hr, true);

                    //Hold here until we get that shutdown event
                    shutdown.WaitOne();

                    Log("Stopping server...");

                    Exit();

                    complete.Set();

                    GC.KeepAlive(hr);
                }
                else
                {
                    Console.CancelKeyPress += (s, e) =>
                    {
                        Log("CancelKeyPress received");
                        e.Cancel = true;
                        shutdown.Set();
                    };

                    AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                    {
                        Log("ProcessExit received");
                        shutdown.Set();
                        complete.Wait();
                    };

                    //Hold here until we get that shutdown event
                    shutdown.WaitOne();

                    Log("Stopping server...");

                    Exit();

                    complete.Set();
                }
            }
            catch (Exception ex)
            {
                Log($"Unhandled Exception: {ex.Message}");
                Log(ex.StackTrace);

                if (ex.InnerException != null)
                {
                    Log("InnerException is " + ex.InnerException.Message);
                    Log(ex.InnerException.StackTrace);
                }
            }
        }

        public static async void DoUpdate()
        {
            _timer.Stop();

            //We want to only find running containers
            var containerListParams = new ContainersListParameters()
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>()
                {
                    {
                        "status", new Dictionary<string, bool>()
                        {
                            {"running", true}
                        }
                    }
                }
            };

            lock (_hostsEntries)
            {
                _hostsEntries = new Dictionary<string, List<string>>();
            }

            //Handle already running containers on the network
            var containers = await _client.Containers.ListContainersAsync(containerListParams);

            foreach (var container in containers)
            {
                if (await ShouldProcessContainer(container.ID))
                    AddHost(container);
            }

            var hash = HostsHashGen();
            if (hash != _hostsHash)
            {
                WriteHosts();
                _hostsHash = hash;
            }

            _timer.Start();
        }

        private static int HostsHashGen()
        {
            var valueHash = new StringBuilder();

            var sortedKeys = _hostsEntries.Keys.OrderBy(s => s);

            foreach (var key in sortedKeys)
            {
                valueHash.Append(key);

                var sortedVals = _hostsEntries[key].OrderBy(s => s);

                foreach (var val in sortedVals)
                {
                    valueHash.Append(val);
                }
            }
            return valueHash.ToString().GetHashCode();
        }

        //Checks whether the container needs to be added to the list or not.  Has to be on the right network
        public static async Task<bool> ShouldProcessContainer(string containerId)
        {
            if (_listenNetwork == CONST_ANYNET)
                return true;

            try
            {
                var response = await _client.Containers.InspectContainerAsync(containerId);

                var networks = response.NetworkSettings.Networks;

                return networks.TryGetValue(_listenNetwork, out _);
            }
            catch (Exception e)
            {
                Log($"Error Checking ShouldProcess: {e.Message}");
                return false;
            }
        }

        private static readonly object _hostLock = new object();

        /// <summary>
        /// Adds the hosts entry to the list for writing later
        /// </summary>
        /// <param name="containerId">The ID of the container to add</param>
        public static void AddHost(ContainerListResponse container)
        {
            lock (_hostLock)
            {
                var hostsForContainer = GetHostsValue(container);

                foreach (var hostsContainerMap in hostsForContainer)
                {
                    if (!_hostsEntries.ContainsKey(hostsContainerMap.Key))
                    {
                        _hostsEntries.Add(hostsContainerMap.Key, hostsForContainer[hostsContainerMap.Key]);
                    }
                    else
                    {
                        if (!_hostsEntries[hostsContainerMap.Key].SequenceEqual(hostsForContainer[hostsContainerMap.Key]))
                        {
                            _hostsEntries[hostsContainerMap.Key].AddRange(hostsForContainer[hostsContainerMap.Key]);
                        }
                    }
                }
            }
        }


        /// <summary>
        ///  Calculates the appropriate hosts entry using all the aliases and 
        /// </summary>
        /// <param name="container">The container to calculate the names for</param>
        /// <returns></returns>
        private static Dictionary<string, List<string>> GetHostsValue(ContainerListResponse container)
        {
            try
            {
                var containerDetails = _client.Containers.InspectContainerAsync(container.ID).Result;

                var hostNames = new List<string> { containerDetails.Config.Hostname };

                var ip = "";

                EndpointSettings network = null;

                //If we care about the actual network or not
                if (_listenNetwork == CONST_ANYNET)
                {
                    foreach (var key in containerDetails.NetworkSettings.Networks.Keys)
                    {
                        network = containerDetails.NetworkSettings.Networks[key];

                        if (network.Aliases != null)
                            hostNames.AddRange(network.Aliases);
                    }
                }
                else
                {
                    network = containerDetails.NetworkSettings.Networks[_listenNetwork];

                    if (network.Aliases != null)
                        hostNames.AddRange(network.Aliases);
                }

                //Filter these out
                hostNames = hostNames.Distinct().ToList();

                foreach (var hostName in hostNames)
                {
                    if (!_termMaps.ContainsKey(hostName))
                    {
                        continue;
                    }

                    var destName = _termMaps[hostName];

                    // Special target: "loopback" (or literal "127.0.0.1") writes the loopback
                    // address instead of resolving a container IP. Needed when Linux containers
                    // run on Docker Desktop for Windows, where docker-internal IPs are not
                    // routable from the Windows host but traefik is port-forwarded on 127.0.0.1.
                    if (destName.Equals("loopback", StringComparison.OrdinalIgnoreCase) || destName == "127.0.0.1")
                    {
                        ip = "127.0.0.1";
                        break;
                    }

                    var allContainers = _client.Containers.ListContainersAsync(new ContainersListParameters()).Result;

                    foreach (var matchContainer in allContainers)
                    {
                        var keys = GetContainerNames(matchContainer);

                        if (!keys.Contains(destName))
                        {
                            continue;
                        }

                        ip = matchContainer.NetworkSettings.Networks[matchContainer.NetworkSettings.Networks.First().Key].IPAddress;
                    }
                }

                hostNames = hostNames.Distinct().ToList();

                //Didn't find anything, we're good!
                if (string.IsNullOrEmpty(ip))
                    ip = network.IPAddress;

                var results = new Dictionary<string, List<string>>();

                results.Add(ip, hostNames);
                return results;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        private static readonly object _lockobject = new object();

        private static List<string> GetContainerNames(ContainerListResponse responseObject)
        {
            var names = new List<string>();
            foreach (var matchNetwork in responseObject.NetworkSettings.Networks)
            {
                if (matchNetwork.Value.Aliases == null)
                    continue;

                names.AddRange(matchNetwork.Value.Aliases);
            }

            if (responseObject.Labels.ContainsKey("com.docker.compose.service"))
                names.Add(responseObject.Labels["com.docker.compose.service"]);

            return names.Select(p => p.ToLower()).Distinct().ToList();
        }

        /// <summary>
        /// Actually write the hosts file, only when dirty.
        /// </summary>
        private static void WriteHosts()
        {
            //Keep some sanity and don't jack with things while in flux.
            lock (_lockobject)
            {
                //1.4 - If we made it this far, we're clear to update
                if (!File.Exists(_hostsPath))
                {
                    Log($"Could not find hosts file at: {_hostsPath}");
                    return;
                }

                var hostsLines = File.ReadAllLines(_hostsPath).ToList();

                var newHostLines = new List<string>();

                Log("Refreshing entries in hosts file");
                //Purge the old ones out
                hostsLines.ForEach(l =>
                {
                    if (!l.EndsWith(GetTail()))
                    {
                        newHostLines.Add(l);
                    }
                });

                //Add the new ones in
                foreach (var ip in _hostsEntries.Keys)
                {
                    foreach (var hostName in _hostsEntries[ip].Distinct())
                    {
                        foreach (var splitHostName in hostName.Split(null))
                        {
                            var entry = $"{ip}\t{splitHostName}";
                            Log($"Adding {entry}");
                            newHostLines.Add($"{entry}\t\t#by {GetTail()}");
                        }
                    }
                }

                File.WriteAllLines(_hostsPath, newHostLines);
            }
        }

        /// <summary>
        /// Returns the unique ID to identify the rows
        /// </summary>
        /// <returns></returns>
        public static string GetTail()
        {
            return !string.IsNullOrEmpty(_sessionId) ? $"whw-{_sessionId}" : "whw";
        }

        /// <summary>
        /// Cleans up the hsots file by nuking the timer and doing one last write with an empty list
        /// </summary>
        public static void Exit()
        {
            Log("Graceful exit");

            _timer.Dispose();

            _hostsEntries = new Dictionary<string, List<string>>();

            WriteHosts();
        }

        private static DockerClient GetClient()
        {
            var defaultEndpoint = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "npipe://./pipe/docker_engine"
                : "unix:///var/run/docker.sock";

            var endpoint = Environment.GetEnvironmentVariable(ENV_ENDPOINT) ?? defaultEndpoint;

            return new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
        }

        private static void Log(string text)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}");
        }
    }
}
