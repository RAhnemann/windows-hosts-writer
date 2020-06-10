using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
        private static string _hostsPath = "c:\\driversetc\\hosts";

        //All host file entries we're tracking
        private static Dictionary<string, string> _hostsEntries = new Dictionary<string, string>();

        //Flag to track whether or not to actually update the hosts file
        private static bool _isDirty = true;

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

            if (Environment.GetEnvironmentVariable(ENV_HOSTPATH) != null)
            {
                _hostsPath = Environment.GetEnvironmentVariable(ENV_HOSTPATH);
                Console.Write($"Overriding hosts path '{_hostsPath}'");
            }

            if (Environment.GetEnvironmentVariable(ENV_NETWORK) != null)
            {
                _listenNetwork = Environment.GetEnvironmentVariable(ENV_NETWORK);
                Console.Write($"Overriding listen network '{_listenNetwork}'");
            }
            else
            {
                Console.WriteLine("Listening to any network");
            }

            if (Environment.GetEnvironmentVariable(ENV_SESSION) != null)
            {
                _sessionId = Environment.GetEnvironmentVariable(ENV_SESSION);
                Console.Write($"Overriding Session Key  '{_sessionId}'");
            }

            if (Environment.GetEnvironmentVariable(ENV_TERMMAP) != null)
            {
                var mapValue = Environment.GetEnvironmentVariable(ENV_TERMMAP);

                var mapSets = mapValue.Split('|');

                foreach (var mapGroup in mapSets)
                {
                    var mapSet = mapGroup.Split(":");

                    if (mapSet.Length != 2)
                    {
                        Console.WriteLine($"Malformed MapSet: '{ mapGroup}'. Expected 'source1,source2:dest'.");
                        continue;
                    }

                    var mapDest = mapSet[1].Trim();

                    foreach (string mapSource in mapSet[0].Split(",", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList())
                    {
                        if (_termMaps.ContainsKey(mapSource))
                        {
                            Console.WriteLine($"Skipping DUplicate Source Map '{mapSource}'");
                            continue;
                        }
                        _termMaps.Add(mapSource.ToLower(), mapDest.ToLower());
                    }
                }

                Console.Write($"Using {_termMaps.Count} termination maps.");
            }

            try
            {
                _client = GetClient();
            }
            catch (Exception ex)
            {
                Log(
                    $"Something went wrong. Likely the Docker engine is not listening at [{_client.Configuration.EndpointBaseUri}] inside of the container.");
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

            Log("Starting Windows Hosts Writer");



            try
            {
                _timer = new System.Timers.Timer(_timerPeriod);

                _timer.Elapsed +=  (s, e)=>{ DoUpdate();} ;

                _timer.Start();
                //_timer = new Timer((s) => DoUpdate(), null, 0, _timerPeriod);

                var shutdown = new ManualResetEvent(false);
                var complete = new ManualResetEventSlim();
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

        private static void _timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            throw new NotImplementedException();
        }

        public static void DoUpdate()
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
                _hostsEntries = new Dictionary<string, string>();
            }

            //Handle already running containers on the network
            var containers = _client.Containers.ListContainersAsync(containerListParams).Result;

            foreach (var container in containers)
            {
                if (ShouldProcessContainer(container.ID))
                    AddHost(container.ID);
            }

            WriteHosts();
            
            _timer.Start();
        }

        //Checks whether the container needs to be added to the list or not.  Has to be on the right network
        public static bool ShouldProcessContainer(string containerId)
        {
            try
            {
                if (_listenNetwork == CONST_ANYNET)
                    return true;

                var response = _client.Containers.InspectContainerAsync(containerId).Result;

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
        public static void AddHost(string containerId)
        {
            lock (_hostLock)
            {
                var hostsValue = GetHostsValue(containerId);

                if (string.IsNullOrWhiteSpace(hostsValue))
                    return;

                if (!_hostsEntries.ContainsKey(containerId))
                {

                    _hostsEntries.Add(containerId, hostsValue);
                    _isDirty = true;

                    return;
                }

                if (_hostsEntries[containerId] != hostsValue)
                {
                    _hostsEntries[containerId] = hostsValue;
                    _isDirty = true;
                }
            }
        }


        /// <summary>
        ///  Calculates the appropriate hosts entry using all the aliases and 
        /// </summary>
        /// <param name="containerId">The ID of the container to calculate</param>
        /// <returns></returns>
        private static string GetHostsValue(string containerId)
        {
            var containerDetails = _client.Containers.InspectContainerAsync(containerId).Result;

            var hostNames = new List<string> { containerDetails.Config.Hostname };

            var IP = "";

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

            //Did we map this elsewhere?
            var hasMap = false;

            foreach (var hostName in hostNames)
            {
                if (_termMaps.ContainsKey(hostName))
                {
                    var destName = _termMaps[hostName];

                    var allContainers = _client.Containers.ListContainersAsync(new ContainersListParameters()).Result;

                    foreach (var matchContainer in allContainers)
                    {
                        var keys = GetContainerNames(matchContainer);

                        if (keys.Contains(destName))
                        {
                            IP = matchContainer.NetworkSettings.Networks[matchContainer.NetworkSettings.Networks.First().Key].IPAddress;

                            if (hasMap)
                                break;
                        }

                    }
                }
            }

            //Didn't find anything, we're good!
            if (string.IsNullOrEmpty(IP))
                IP = network.IPAddress;

            var sb = new StringBuilder();
            for (int i = 0; i < hostNames.Count; i++)
            {
                sb.Append($"{IP}\t{hostNames[i]}\t\t#{containerId} by {GetTail()}");
                if (i != hostNames.Count - 1) // Don't append new line for the last element
                    sb.Append(Environment.NewLine);
            }

            return sb.ToString();
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
                //Do what we need, only when we need to.
                if (!_isDirty)
                    return;

                //Keep from repeating
                _isDirty = false;


                if (!File.Exists(_hostsPath))
                {
                    Log($"Could not find hosts file at: {_hostsPath}");
                    return;
                }

                var hostsLines = File.ReadAllLines(_hostsPath).ToList();

                var newHostLines = new List<string>();

                //Purge the old ones out
                hostsLines.ForEach(l =>
                {
                    if (!l.EndsWith(GetTail()))
                    {
                        newHostLines.Add(l);
                    }
                });

                //Add the new ones in
                foreach (var entry in _hostsEntries)
                {
                    newHostLines.Add(entry.Value);
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

            _hostsEntries = new Dictionary<string, string>();
            _isDirty = true;
            WriteHosts();
        }

        private static DockerClient GetClient()
        {
            var endpoint = Environment.GetEnvironmentVariable(ENV_ENDPOINT) ?? "npipe://./pipe/docker_engine";

            return new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
        }

        private static void Log(string text)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss:fff}: {text}");
        }
    }
}
